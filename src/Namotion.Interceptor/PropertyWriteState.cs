namespace Namotion.Interceptor;

/// <summary>
/// The mutable per-property state the write terminal maintains, kept as a single property data entry
/// because it is read on every delivered change and each lookup hashes the property name and the key,
/// so splitting it would double that cost.
/// </summary>
/// <remarks>
/// Written under the subject's lock, read without it. The 64-bit fields go through
/// <see cref="Interlocked"/> because netstandard2.0 includes 32-bit runtimes, where a plain
/// <c>long</c> store can tear.
/// </remarks>
internal sealed class PropertyWriteState
{
    internal long TimestampTicks;

    /// <summary>
    /// The revision of the last write to this property that reached a terminal and did NOT come from a
    /// source.
    /// </summary>
    /// <remarks>
    /// The exclusion is load-bearing, not an optimization. A change may be dropped only because a later
    /// commit carries the settled value in its place, and a source-originated commit is skipped as an
    /// echo when that source's queue is drained, so counting it would drop a write that nothing then
    /// delivers. See issue #373.
    /// <para>
    /// Confirmed commits do advance it even though they are echo-skipped too. They are safe for a
    /// different reason: the transaction writer stamps Confirmed only after the source write succeeded,
    /// so the source already holds that value. Do not "fix" the asymmetry in either direction.
    /// </para>
    /// </remarks>
    internal long LastNonSourceCommitRevision;

    /// <summary>
    /// Whether a sink has published this property's value. Sticky. Volatile because a stale read of
    /// false skips a transaction confirmation write-back, leaving the divergence it exists to repair.
    /// </summary>
    internal volatile bool Published;
}
