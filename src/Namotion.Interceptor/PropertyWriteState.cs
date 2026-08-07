namespace Namotion.Interceptor;

/// <summary>
/// The mutable per-property state the write terminal maintains, held as one entry in the subject's
/// property data so that a reader who needs more than one field pays a single lookup. That matters:
/// the entry is read on every change a connector delivers, and a property data lookup hashes both the
/// property name and the key, so a second entry would double the per-change cost.
/// </summary>
/// <remarks>
/// Created on the property's first write and reused for its lifetime. Fields are written under the
/// subject's lock by the terminal, but read without it, which is why the 64-bit ones go through
/// <see cref="Interlocked"/>: the library targets netstandard2.0, so it runs on 32-bit runtimes where
/// ECMA-335 does not guarantee that a plain <c>long</c> store is atomic.
/// </remarks>
internal sealed class PropertyWriteState
{
    /// <summary>
    /// The write timestamp in raw UTC ticks, or 0 when none has been recorded.
    /// </summary>
    internal long TimestampTicks;

    /// <summary>
    /// The revision of the last write to this property that reached a terminal and did NOT come from a
    /// source, or 0 when no such write has happened.
    /// </summary>
    /// <remarks>
    /// Source-originated commits are excluded on purpose, and the exclusion is load-bearing rather than
    /// an optimization. A connector drops a change only because a later commit will carry the settled
    /// value in its place, and a commit that came from a source is skipped as an echo when that source's
    /// queue is drained. Counting it would drop a newer local write against a superseding change that is
    /// then never delivered, so the write is lost permanently and both ends settle on the old value. That
    /// is reachable without concurrency: a source echoing back a write we just made, arriving after a
    /// second write to the same property, is enough (see issue #373).
    /// </remarks>
    internal long LastNonSourceCommitRevision;

    /// <summary>
    /// Whether a sink has published this property's value. Sticky, and never cleared: it only ever
    /// decides whether a transaction confirmation is worth writing back, and a confirmation carries the
    /// current value, so the worst an over-eager mark can cost is one redundant write of the value the
    /// sink is owed anyway. A plain bool needs no interlocking, because a torn read is impossible and a
    /// stale read costs at most that same redundant write.
    /// </summary>
    internal bool Published;
}
