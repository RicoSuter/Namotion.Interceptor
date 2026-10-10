namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The limit for a single call into the storage. Without it a stalled source would block the worker,
/// and with it every operation that waits behind the call.
/// </summary>
/// <remarks>
/// Only the wait ends: the call is not cancelled and finishes or fails on its own. Calls on the storage client
/// are limited by <see cref="TimeLimitedBlobStorage"/>, which reports the limit as
/// <see cref="StorageUnresponsiveException"/>. The methods here limit the reading of a stream to its end, for
/// the reads that run on the storage worker, and report the limit as a plain <see cref="TimeoutException"/>:
/// one file that is not read in time says nothing about the other files. Not limited is a stream that other
/// code reads after <see cref="FluentStorageContainer.ReadBlobAsync"/>, for example a download in the UI. That
/// does not run on the worker.
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

    /// <summary>
    /// Limits the reading of a stream of the storage, which is not a call on the client.
    /// </summary>
    /// <param name="call">The read that is running.</param>
    /// <param name="operation">What is being done with the file, for the message of the exception.</param>
    /// <param name="path">The file that is read.</param>
    /// <param name="timeProvider">The clock of the limit.</param>
    /// <param name="cancellationToken">Ends the wait with the caller's cancellation.</param>
    /// <exception cref="TimeoutException">
    /// The read did not complete within <see cref="Limit"/>. Never a <see cref="StorageUnresponsiveException"/>.
    /// </exception>
    public static Task<TResult> WithStorageTimeoutAsync<TResult>(
        this Task<TResult> call, string operation, string path, TimeProvider timeProvider, CancellationToken cancellationToken)
        => call.IsCompleted ? call : LimitAsync(call, operation, path, timeProvider, cancellationToken);

    /// <inheritdoc cref="WithStorageTimeoutAsync{TResult}"/>
    public static Task WithStorageTimeoutAsync(
        this Task call, string operation, string path, TimeProvider timeProvider, CancellationToken cancellationToken)
        => call.IsCompleted ? call : LimitAsync(call, operation, path, timeProvider, cancellationToken);

    /// <summary>
    /// Marks the fault of a call that nobody waits for any more as observed.
    /// </summary>
    public static void ObserveFault(this Task call)
        => call.ContinueWith(
            static completed => completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static async Task<TResult> LimitAsync<TResult>(
        Task<TResult> call, string operation, string path, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        await LimitAsync((Task)call, operation, path, timeProvider, cancellationToken);
        return await call;
    }

    private static async Task LimitAsync(
        Task call, string operation, string path, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!await call.WaitWithinLimitAsync(timeProvider, cancellationToken))
        {
            // Its stream is disposed while it still reads, so it usually fails later.
            call.ObserveFault();

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"The storage did not complete {operation} '{path}' within {Limit.TotalSeconds:0} seconds.");
        }

        await call;
    }
}
