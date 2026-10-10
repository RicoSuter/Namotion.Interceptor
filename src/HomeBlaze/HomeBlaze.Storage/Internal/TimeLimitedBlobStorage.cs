using FluentStorage;
using FluentStorage.Blobs;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Makes every call into a storage return within <see cref="StorageCallTimeout.Limit"/>, also when the storage
/// blocks inside the call. A call that does not complete in time is given up, and until it has returned every
/// further call fails at once.
/// </summary>
/// <remarks>
/// A call that was given up is not cancelled. It finishes or fails on its own, so a write can still land after
/// its caller got the failure. It then reads from the stream it was given: a caller that has disposed that
/// stream meanwhile is left with an empty or partial file.
/// </remarks>
internal sealed class TimeLimitedBlobStorage : IBlobStorage
{
    private readonly IBlobStorage _inner;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    private int _abandonedCallCount;

    public TimeLimitedBlobStorage(IBlobStorage inner, TimeProvider timeProvider, ILogger? logger)
    {
        _inner = inner;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// True while a call that was given up has not returned. Every further call is refused until it has.
    /// </summary>
    public bool IsUnresponsive => Volatile.Read(ref _abandonedCallCount) > 0;

    /// <inheritdoc cref="OpenReadAsync"/>
    public Task<IReadOnlyCollection<Blob>> ListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
        => RunAsync(nameof(ListAsync), options?.FolderPath ?? "/", () => _inner.ListAsync(options, cancellationToken), cancellationToken);

    /// <inheritdoc cref="OpenReadAsync"/>
    public Task WriteAsync(string fullPath, Stream dataStream, bool append = false, CancellationToken cancellationToken = default)
        => RunAsync(nameof(WriteAsync), fullPath, () => _inner.WriteAsync(fullPath, dataStream, append, cancellationToken), cancellationToken);

    /// <exception cref="StorageUnresponsiveException">
    /// The storage did not complete the call in time, or the call was refused because <see cref="IsUnresponsive"/> is true.
    /// </exception>
    public Task<Stream> OpenReadAsync(string fullPath, CancellationToken cancellationToken = default)
        => RunAsync(nameof(OpenReadAsync), fullPath, () => _inner.OpenReadAsync(fullPath, cancellationToken), cancellationToken);

    /// <inheritdoc cref="OpenReadAsync"/>
    public Task DeleteAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => RunAsync(nameof(DeleteAsync), fullPaths, () => _inner.DeleteAsync(fullPaths, cancellationToken), cancellationToken);

    /// <inheritdoc cref="OpenReadAsync"/>
    public Task<IReadOnlyCollection<bool>> ExistsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => RunAsync(nameof(ExistsAsync), fullPaths, () => _inner.ExistsAsync(fullPaths, cancellationToken), cancellationToken);

    /// <inheritdoc cref="OpenReadAsync"/>
    public Task<IReadOnlyCollection<Blob>> GetBlobsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => RunAsync(nameof(GetBlobsAsync), fullPaths, () => _inner.GetBlobsAsync(fullPaths, cancellationToken), cancellationToken);

    /// <inheritdoc cref="OpenReadAsync"/>
    public Task SetBlobsAsync(IEnumerable<Blob> blobs, CancellationToken cancellationToken = default)
        => RunAsync(nameof(SetBlobsAsync), null, () => _inner.SetBlobsAsync(blobs, cancellationToken), cancellationToken);

    /// <inheritdoc cref="OpenReadAsync"/>
    public Task<ITransaction> OpenTransactionAsync()
        => RunAsync(nameof(OpenTransactionAsync), null, () => _inner.OpenTransactionAsync(), CancellationToken.None);

    public void Dispose() => _inner.Dispose();

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

    /// <param name="operation">The member that is called, for the message of a failure.</param>
    /// <param name="target">The path or the paths that the call is about, for the message of a failure.</param>
    /// <param name="start">Starts the call on the storage.</param>
    /// <param name="cancellationToken">Ends the wait with the caller's cancellation.</param>
    private async Task<TResult> RunAsync<TResult>(
        string operation, object? target, Func<Task<TResult>> start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Started, it would block one more thread of the pool for as long as the storage stalls. And a write
        // that was given up could finish after a later write of the same file and overwrite it.
        if (IsUnresponsive)
        {
            throw new StorageUnresponsiveException(
                $"The storage is unresponsive, {Describe(operation, target)} was not started: an earlier call was given up and has not returned yet.");
        }

        // On the thread pool, because a storage can do its work before it returns its task.
        var call = Task.Run(start, CancellationToken.None);
        if (await call.WaitWithinLimitAsync(_timeProvider, cancellationToken))
        {
            return await call;
        }

        var description = Describe(operation, target);
        Abandon(description, call);

        cancellationToken.ThrowIfCancellationRequested();
        throw new StorageUnresponsiveException(
            $"The storage did not complete {description} within {StorageCallTimeout.Limit.TotalSeconds:0} seconds.");
    }

    private static string Describe(string operation, object? target)
        => target switch
        {
            string path => $"{operation} for '{path}'",
            IEnumerable<string> paths => $"{operation} for '{string.Join("', '", paths)}'",
            _ => operation
        };

    private void Abandon<TResult>(string description, Task<TResult> call)
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
                        _logger?.LogDebug(exception.GetBaseException(), "A storage call that was given up failed later: {Call}", description);
                    }
                    else if (completed is { IsCompletedSuccessfully: true, Result: IDisposable result })
                    {
                        // Nobody else can close what the call still returned, such as the stream of a late open.
                        result.Dispose();
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _abandonedCallCount);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
