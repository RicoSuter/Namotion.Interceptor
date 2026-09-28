namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Process-wide gate for the per-property listener lookup. Counts subscriptions created and not yet
/// disposed. It is a long (not int) so cumulative fire-and-forget increments (which never decrement)
/// cannot wrap to zero and spuriously close the gate; 2^63 registrations is unreachable.
/// </summary>
internal static class PropertyChangeSubscriptions
{
    private static long _subscriptionCount;

    internal static void IncrementSubscriptionCount() => Interlocked.Increment(ref _subscriptionCount);

    internal static void DecrementSubscriptionCount() => Interlocked.Decrement(ref _subscriptionCount);

    // Atomic 64-bit read. Volatile.Read (a plain acquire load, atomic for long on all supported
    // runtimes) and NOT Interlocked.Read: Interlocked.Read is CompareExchange(ref, 0, 0), an RMW
    // that dirties the cache line and contends across writer cores on every write. The write path's
    // post-commit re-check gets its StoreLoad ordering from an explicit Interlocked.MemoryBarrier()
    // BEFORE calling this (core-local, no shared-line write); it is the read side of the Dekker
    // pairing with IncrementSubscriptionCount-then-install in PropertyChangeSubscription.Create.
    internal static long ReadSubscriptionCount() => Volatile.Read(ref _subscriptionCount);

    // Test-only reset hook (used by the serialized per-property test collection).
    internal static void ResetForTests() => Interlocked.Exchange(ref _subscriptionCount, 0);
}
