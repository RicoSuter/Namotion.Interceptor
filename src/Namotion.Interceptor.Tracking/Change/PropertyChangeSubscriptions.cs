namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Process-wide gate for the per-property listener lookup. Counts subscriptions created and not yet
/// disposed. It is a long (not int) so cumulative fire-and-forget increments (which never decrement)
/// cannot wrap to zero and spuriously close the gate; 2^63 registrations is unreachable.
/// </summary>
internal static class PropertyChangeSubscriptions
{
    private static long _liveCount;

    public static void IncrementLiveCount() => Interlocked.Increment(ref _liveCount);

    public static void DecrementLiveCount() => Interlocked.Decrement(ref _liveCount);

    // Atomic 64-bit read. Volatile.Read (a plain acquire load, atomic for long on all supported
    // runtimes) and NOT Interlocked.Read: Interlocked.Read is CompareExchange(ref, 0, 0), an RMW
    // that dirties the cache line and contends across writer cores on every write. The write path's
    // post-commit re-check gets its StoreLoad ordering from an explicit Interlocked.MemoryBarrier()
    // BEFORE calling this (core-local, no shared-line write); the Dekker pairing with
    // IncrementLiveCount-then-install is documented in the spec's Fast-path rules.
    public static long ReadLiveCount() => Volatile.Read(ref _liveCount);

    // Test-only reset hook (see the serialized test collection in Task 4).
    internal static void ResetForTests() => Interlocked.Exchange(ref _liveCount, 0);
}
