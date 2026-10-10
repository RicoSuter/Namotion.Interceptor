namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The limit for a single call into the storage. Without it a stalled source would block the worker,
/// and with it every operation that waits behind the call.
/// </summary>
/// <remarks>
/// Only the wait ends: the call is not cancelled and finishes or fails on its own, so a write can still land
/// after its caller got the timeout. The limit starts when the call has returned its task. A backend that
/// blocks inside the call itself is not limited.
/// </remarks>
internal static class StorageCallTimeout
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <exception cref="TimeoutException">The call did not complete within <see cref="Limit"/>.</exception>
    public static Task<TResult> WithStorageTimeoutAsync<TResult>(
        this Task<TResult> call, TimeProvider timeProvider, CancellationToken cancellationToken)
        => call.WaitAsync(Limit, timeProvider, cancellationToken);

    /// <exception cref="TimeoutException">The call did not complete within <see cref="Limit"/>.</exception>
    public static Task WithStorageTimeoutAsync(
        this Task call, TimeProvider timeProvider, CancellationToken cancellationToken)
        => call.WaitAsync(Limit, timeProvider, cancellationToken);
}
