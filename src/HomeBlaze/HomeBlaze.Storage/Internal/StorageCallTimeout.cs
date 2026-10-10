namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The limit for a single call into the storage. Without it a stalled source would block the worker,
/// and with it every operation that waits behind the call.
/// </summary>
/// <remarks>
/// Only the wait ends: the call is not cancelled and finishes or fails on its own. Calls on the storage client
/// are limited by <see cref="TimeLimitedBlobStorage"/>, and the methods here limit the reading of a stream to
/// its end. Not limited is subject code that reads a stream it got from the storage.
/// </remarks>
internal static class StorageCallTimeout
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits for the call, but no longer than <see cref="Limit"/>.
    /// </summary>
    /// <returns>False when the wait ended before the call did, by the limit or by cancellation.</returns>
    public static async Task<bool> WaitWithinLimitAsync(
        this Task call, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (call.IsCompleted)
        {
            return true;
        }

        using var limit = new CancellationTokenSource(Limit, timeProvider);
        using var waitEnded = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancellationToken);

        await call.WaitAsync(waitEnded.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return call.IsCompleted;
    }

    /// <exception cref="TimeoutException">The call did not complete within <see cref="Limit"/>.</exception>
    public static Task<TResult> WithStorageTimeoutAsync<TResult>(
        this Task<TResult> call, TimeProvider timeProvider, CancellationToken cancellationToken)
        => call.IsCompleted ? call : LimitAsync(call, timeProvider, cancellationToken);

    /// <exception cref="TimeoutException">The call did not complete within <see cref="Limit"/>.</exception>
    public static Task WithStorageTimeoutAsync(
        this Task call, TimeProvider timeProvider, CancellationToken cancellationToken)
        => call.IsCompleted ? call : LimitAsync(call, timeProvider, cancellationToken);

    private static async Task<TResult> LimitAsync<TResult>(
        Task<TResult> call, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        await LimitAsync((Task)call, timeProvider, cancellationToken);
        return await call;
    }

    private static async Task LimitAsync(Task call, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!await call.WaitWithinLimitAsync(timeProvider, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"The storage did not deliver the content within {Limit.TotalSeconds:0} seconds.");
        }

        await call;
    }
}
