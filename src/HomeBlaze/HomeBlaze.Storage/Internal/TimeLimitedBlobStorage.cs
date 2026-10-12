using FluentStorage;
using FluentStorage.Blobs;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Makes every call into a storage return within <see cref="StorageCallTimeout.Limit"/>, also when the storage
/// blocks inside the call. A call that does not complete in time is given up, and until it has returned every
/// further call is refused. Both fail with a <see cref="StorageUnresponsiveException"/>.
/// </summary>
/// <remarks>
/// A call that was given up is not cancelled. It finishes or fails on its own, so a write can still land after
/// its caller got the failure. It then still reads from the stream it was given, so that stream must stay
/// readable after the failure.
/// </remarks>
internal sealed class TimeLimitedBlobStorage : IBlobStorage
{
    private readonly IBlobStorage _inner;
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
    /// be examined. For a storage that lists such a root as empty, and that creates the root again when it is
    /// asked about a path in a folder or writes one.
    /// </param>
    /// <param name="whenAnswersAgain">
    /// Runs each time the last call that was given up has returned, so that calls are taken again. It runs on
    /// the thread on which that call ended and must neither block nor throw.
    /// </param>
    public TimeLimitedBlobStorage(
        IBlobStorage inner, TimeProvider timeProvider, ILogger? logger, Action? verifyRoot = null, Action? whenAnswersAgain = null)
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

    public Task<IReadOnlyCollection<Blob>> ListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
        => RunAsync(nameof(ListAsync), options?.FolderPath ?? "/", () => VerifyRoot().ListAsync(options, cancellationToken), cancellationToken);

    public Task WriteAsync(string fullPath, Stream dataStream, bool append = false, CancellationToken cancellationToken = default)
        => RunAsync(nameof(WriteAsync), fullPath, () => VerifyRoot().WriteAsync(fullPath, dataStream, append, cancellationToken), cancellationToken);

    public Task<Stream> OpenReadAsync(string fullPath, CancellationToken cancellationToken = default)
        => RunAsync(nameof(OpenReadAsync), fullPath, () => VerifyRoot().OpenReadAsync(fullPath, cancellationToken), cancellationToken);

    public Task DeleteAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => RunAsync(nameof(DeleteAsync), fullPaths, () => VerifyRoot().DeleteAsync(fullPaths, cancellationToken), cancellationToken);

    public Task<IReadOnlyCollection<bool>> ExistsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => RunAsync(nameof(ExistsAsync), fullPaths, () => VerifyRoot().ExistsAsync(fullPaths, cancellationToken), cancellationToken);

    public Task<IReadOnlyCollection<Blob>> GetBlobsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => RunAsync(nameof(GetBlobsAsync), fullPaths, () => VerifyRoot().GetBlobsAsync(fullPaths, cancellationToken), cancellationToken);

    public Task SetBlobsAsync(IEnumerable<Blob> blobs, CancellationToken cancellationToken = default)
        => RunAsync(nameof(SetBlobsAsync), null, () => VerifyRoot().SetBlobsAsync(blobs, cancellationToken), cancellationToken);

    public Task<ITransaction> OpenTransactionAsync()
        => RunAsync(nameof(OpenTransactionAsync), null, () => _inner.OpenTransactionAsync(), CancellationToken.None);

    public void Dispose() => _inner.Dispose();

    // Within the call, so that a root that stalls when it is examined is limited like the call itself.
    private IBlobStorage VerifyRoot()
    {
        _verifyRoot?.Invoke();
        return _inner;
    }

    private Task RunAsync(string operation, object? target, Func<Task> start, CancellationToken cancellationToken)
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
        string operation, object? target, Func<Task<TResult>> start, CancellationToken cancellationToken)
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

        // Counted before the text of the failure is built: that enumerates paths of the caller and can fail.
        Abandon(operation, target, call);

        cancellationToken.ThrowIfCancellationRequested();
        throw new StorageUnresponsiveException(
            $"The storage did not complete {Describe(operation, target)} within {StorageCallTimeout.Limit.TotalSeconds:0} seconds.", this, isRefusal: false);
    }

    private static string Describe(string operation, object? target)
        => target switch
        {
            string path => $"{operation} for '{path}'",
            IEnumerable<string> paths => $"{operation} for '{string.Join("', '", paths)}'",
            _ => operation
        };

    private void Abandon<TResult>(string operation, object? target, Task<TResult> call)
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
