namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The limit for a single call into the storage. Without it a stalled source would block the worker,
/// and with it every operation that waits behind the call.
/// </summary>
/// <remarks>
/// Only the wait ends: the call is not cancelled and finishes or fails on its own. Calls on the storage client
/// are limited by <see cref="TimeLimitedBlobStorage"/>, which reports the limit as
/// <see cref="StorageUnresponsiveException"/>. <see cref="WithStorageTimeoutAsync"/> limits the reading of a
/// stream to its end and reports the limit as a plain <see cref="TimeoutException"/>: one file that is not read
/// in time says nothing about the other files.
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

        // Sources of its own and not Task.WaitAsync with a time limit: that one releases its timer only after
        // the code that waited has gone on, so the timer of a wait that is over could still be armed when the
        // next wait starts. Released here, the armed timers of the clock are exactly those of the waits that run.
        using var limit = new CancellationTokenSource(Limit, timeProvider);
        using var waitEnded = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancellationToken);

        await call.WaitAsync(waitEnded.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return call.IsCompleted;
    }

    /// <summary>
    /// Waits for the reading of a stream of the storage, which is not a call on the client, and completes or
    /// fails with it.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// The read did not complete within <see cref="Limit"/>. Never a <see cref="StorageUnresponsiveException"/>.
    /// </exception>
    public static async Task WithStorageTimeoutAsync(
        this Task call, string operation, string path, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!await call.WaitWithinLimitAsync(timeProvider, cancellationToken))
        {
            // Its stream is disposed while it still reads, so it usually fails later. Reading the fault marks
            // it as observed.
            _ = call.ContinueWith(
                static completed => completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"The storage did not complete {operation} '{path}' within {Limit.TotalSeconds:0} seconds.");
        }

        await call;
    }
}
