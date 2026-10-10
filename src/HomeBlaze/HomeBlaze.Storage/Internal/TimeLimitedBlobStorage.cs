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
/// its caller got the failure.
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
        => RunAsync(nameof(ListAsync), () => _inner.ListAsync(options, cancellationToken), cancellationToken);

    /// <inheritdoc cref="OpenReadAsync"/>
    public Task WriteAsync(string fullPath, Stream dataStream, bool append = false, CancellationToken cancellationToken = default)
        => RunAsync(nameof(WriteAsync), () => _inner.WriteAsync(fullPath, dataStream, append, cancellationToken), cancellationToken);

    /// <exception cref="StorageUnresponsiveException">
    /// The storage did not complete the call in time, or the call was refused because <see cref="IsUnresponsive"/> is true.
    /// </exception>
    public Task<Stream> OpenReadAsync(string fullPath, CancellationToken cancellationToken = default)
        => RunAsync(nameof(OpenReadAsync), () => _inner.OpenReadAsync(fullPath, cancellationToken), cancellationToken);

    /// <inheritdoc cref="OpenReadAsync"/>
    public Task DeleteAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => RunAsync(nameof(DeleteAsync), () => _inner.DeleteAsync(fullPaths, cancellationToken), cancellationToken);

    /// <inheritdoc cref="OpenReadAsync"/>
    public Task<IReadOnlyCollection<bool>> ExistsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => RunAsync(nameof(ExistsAsync), () => _inner.ExistsAsync(fullPaths, cancellationToken), cancellationToken);

    /// <inheritdoc cref="OpenReadAsync"/>
    public Task<IReadOnlyCollection<Blob>> GetBlobsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => RunAsync(nameof(GetBlobsAsync), () => _inner.GetBlobsAsync(fullPaths, cancellationToken), cancellationToken);

    /// <inheritdoc cref="OpenReadAsync"/>
    public Task SetBlobsAsync(IEnumerable<Blob> blobs, CancellationToken cancellationToken = default)
        => RunAsync(nameof(SetBlobsAsync), () => _inner.SetBlobsAsync(blobs, cancellationToken), cancellationToken);

    /// <inheritdoc cref="OpenReadAsync"/>
    public Task<ITransaction> OpenTransactionAsync()
        => RunAsync(nameof(OpenTransactionAsync), () => _inner.OpenTransactionAsync(), CancellationToken.None);

    public void Dispose() => _inner.Dispose();

    private Task RunAsync(string operation, Func<Task> start, CancellationToken cancellationToken)
        => RunAsync(
            operation,
            async () =>
            {
                await start();
                return true;
            },
            cancellationToken);

    private async Task<TResult> RunAsync<TResult>(
        string operation, Func<Task<TResult>> start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Started, it would block one more thread of the pool for as long as the storage stalls. And a write
        // that was given up could finish after a later write of the same file and overwrite it.
        if (IsUnresponsive)
        {
            throw new StorageUnresponsiveException(
                $"The storage is unresponsive, {operation} was not started: an earlier call was given up and has not returned yet.");
        }

        // On the thread pool, because a storage can do its work before it returns its task.
        var call = Task.Run(start, CancellationToken.None);
        if (await call.WaitWithinLimitAsync(_timeProvider, cancellationToken))
        {
            return await call;
        }

        Abandon(operation, call);

        cancellationToken.ThrowIfCancellationRequested();
        throw new StorageUnresponsiveException(
            $"The storage did not complete {operation} within {StorageCallTimeout.Limit.TotalSeconds:0} seconds.");
    }

    private void Abandon<TResult>(string operation, Task<TResult> call)
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
                        _logger?.LogDebug(exception.GetBaseException(), "A storage call that was given up failed later: {Operation}", operation);
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
