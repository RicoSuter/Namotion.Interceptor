using FluentStorage.Model;
using FluentStorage.Storage;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Makes every call into a storage return within <see cref="StorageCallTimeout.Limit"/>, also when the storage
/// blocks inside the call. A call that does not complete in time is given up, and until it has returned every
/// further call is refused. Both fail with a <see cref="StorageUnresponsiveException"/>.
/// </summary>
/// <remarks>
/// <para>
/// A call that was given up is not canceled. It finishes or fails on its own, so a write can still land after
/// its caller got the failure. It then still reads from the stream it was given, so that stream must stay
/// readable after the failure.
/// </para>
/// <para>
/// Only <see cref="ListObjects"/>, <see cref="SetObject(string, Stream, bool, CancellationToken)"/>,
/// <see cref="OpenRead"/>, <see cref="DeleteObject"/>, <see cref="ObjectExists"/>, <see cref="GetObjectInfo"/>
/// and <see cref="Dispose"/> reach the inner storage. Every other member is that of <see cref="StoreBase"/>:
/// some build on these, others throw or return a default without reaching the inner storage, so a new call
/// into the storage must be one of these or be overridden here.
/// </para>
/// </remarks>
internal sealed class TimeLimitedStore : StoreBase
{
    private readonly IStore _inner;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;
    private readonly Action? _verifyRoot;
    private readonly Action? _whenAnswersAgain;

    private int _abandonedCallCount;

    /// <param name="inner">The storage whose calls are limited.</param>
    /// <param name="timeProvider">The clock of the limit.</param>
    /// <param name="logger">Receives what a call that was given up does later.</param>
    /// <param name="verifyRoot">
    /// Runs as the first part of every call that names a path and throws when the root of the storage cannot
    /// be examined. The call then fails with a <see cref="StorageRootUnavailableException"/>. For a storage
    /// that lists such a root as empty, and that creates the root again when it writes a path, or reads or
    /// deletes a path in a folder.
    /// </param>
    /// <param name="whenAnswersAgain">
    /// Runs each time the last call that was given up has returned, so that calls are taken again. It runs on
    /// the thread on which that call ended and must neither block nor throw.
    /// </param>
    public TimeLimitedStore(
        IStore inner, TimeProvider timeProvider, ILogger? logger, Action? verifyRoot = null, Action? whenAnswersAgain = null)
    {
        _inner = inner;
        _timeProvider = timeProvider;
        _logger = logger;
        _verifyRoot = verifyRoot;
        _whenAnswersAgain = whenAnswersAgain;
    }

    /// <summary>
    /// True while a call that was given up has not returned. Every further call is refused until it has.
    /// </summary>
    public bool IsUnresponsive => Volatile.Read(ref _abandonedCallCount) > 0;

    public override Task<List<StoreObject>> ListObjects(StorageListOptions? options = null, CancellationToken cancellationToken = default)
        => RunAsync(
            nameof(ListObjects),
            string.IsNullOrEmpty(options?.FolderPath) ? "/" : options.FolderPath,
            () => VerifyRoot().ListObjects(options, cancellationToken),
            cancellationToken);

    public override Task SetObject(string objectPath, Stream sourceStream, bool append, CancellationToken cancellationToken = default)
        => RunAsync(nameof(SetObject), objectPath, () => VerifyRoot().SetObject(objectPath, sourceStream, append, cancellationToken), cancellationToken);

    public override Task<Stream> OpenRead(string objectPath, CancellationToken cancellationToken = default)
        => RunAsync(nameof(OpenRead), objectPath, () => VerifyRoot().OpenRead(objectPath, cancellationToken), cancellationToken);

    public override Task DeleteObject(string objectPath, CancellationToken cancellationToken = default)
        => RunAsync(nameof(DeleteObject), objectPath, () => VerifyRoot().DeleteObject(objectPath, cancellationToken), cancellationToken);

    public override Task<bool> ObjectExists(string objectPath, CancellationToken cancellationToken = default)
        => RunAsync(nameof(ObjectExists), objectPath, () => VerifyRoot().ObjectExists(objectPath, cancellationToken), cancellationToken);

    public override Task<StoreObject> GetObjectInfo(string objectPath, CancellationToken cancellationToken = default)
        => RunAsync(nameof(GetObjectInfo), objectPath, () => VerifyRoot().GetObjectInfo(objectPath, cancellationToken), cancellationToken);

    public override void Dispose() => _inner.Dispose();

    // Within the call, so that a root that stalls when it is examined is limited like the call itself.
    private IStore VerifyRoot()
    {
        try
        {
            _verifyRoot?.Invoke();
        }
        catch (Exception exception)
        {
            // One type for whatever the check throws: it is a failure of the storage, not of the path of the call.
            throw new StorageRootUnavailableException(this, exception);
        }

        return _inner;
    }

    private Task RunAsync(string operation, string target, Func<Task> start, CancellationToken cancellationToken)
        => RunAsync(
            operation,
            target,
            async () =>
            {
                await start();
                return true;
            },
            cancellationToken);

    private async Task<TResult> RunAsync<TResult>(
        string operation, string target, Func<Task<TResult>> start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Started, it would block one more thread of the pool for as long as the storage stalls. And a write
        // that was given up could finish after a later write of the same file and overwrite it.
        if (IsUnresponsive)
        {
            throw new StorageUnresponsiveException(
                $"The storage is unresponsive, {Describe(operation, target)} was not started: an earlier call was given up and has not returned yet.", this, isRefusal: true);
        }

        // On the thread pool, because a storage can do its work before it returns its task.
        var call = Task.Run(start, CancellationToken.None);
        if (await call.WaitWithinLimitAsync(_timeProvider, cancellationToken))
        {
            return await call;
        }

        Abandon(operation, target, call);

        cancellationToken.ThrowIfCancellationRequested();
        throw new StorageUnresponsiveException(
            $"The storage did not complete {Describe(operation, target)} within {StorageCallTimeout.Limit.TotalSeconds:0} seconds.", this, isRefusal: false);
    }

    private static string Describe(string operation, string target)
        => $"{operation} for '{target}'";

    private void Abandon<TResult>(string operation, string target, Task<TResult> call)
    {
        Interlocked.Increment(ref _abandonedCallCount);

        _ = call.ContinueWith(
            completed =>
            {
                try
                {
                    // Reading the exception marks it as observed.
                    if (completed.Exception is { } exception)
                    {
                        _logger?.LogDebug(exception.GetBaseException(), "A storage call that was given up failed later: {Call}", Describe(operation, target));
                    }
                    else if (completed is { IsCompletedSuccessfully: true, Result: IDisposable result })
                    {
                        // Nobody else can close what the call still returned, such as the stream of a late open.
                        result.Dispose();
                    }
                }
                finally
                {
                    if (Interlocked.Decrement(ref _abandonedCallCount) == 0)
                    {
                        _whenAnswersAgain?.Invoke();
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
