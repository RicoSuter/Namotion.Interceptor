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

    // Atomic 64-bit read (matches PropertyReference.SetWriteTimestamp's Interlocked.Read pattern).
    // Use this accessor so the field stays private. Interlocked.Read is correct on every runtime; on
    // the common 64-bit target it lowers to a single load, so it is hot-path safe (a plain 64-bit read
    // could tear on a 32-bit runtime, and volatile is not applicable to a 64-bit field, hence the
    // explicit atomic read).
    public static long ReadLiveCount() => Interlocked.Read(ref _liveCount);

    // Test-only reset hook (see the serialized test collection in Task 4).
    internal static void ResetForTests() => Interlocked.Exchange(ref _liveCount, 0);
}
