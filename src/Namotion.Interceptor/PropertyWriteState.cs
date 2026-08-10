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
    /// delivers.
    /// <para>
    /// Confirmed commits do advance it even though they are echo-skipped too. They are safe for a
    /// different reason: the transaction writer stamps Confirmed only after the source write succeeded,
    /// so the source already holds that value. Do not "fix" the asymmetry in either direction.
    /// </para>
    /// </remarks>
    internal long LastNonSourceCommitRevision;

    /// <summary>
    /// The revision of the last source-originated write to this property that reached a terminal.
    /// </summary>
    /// <remarks>
    /// Kept disjoint from <see cref="LastNonSourceCommitRevision"/> rather than as a combined
    /// last-of-any-kind field so that a commit writes exactly one of the two: recording the revision costs
    /// one interlocked store rather than two, on top of the timestamp store the write path already had.
    /// The last commit of any kind is their maximum, computed on the read side, which runs per delivered
    /// change rather than per write.
    /// <para>
    /// Only a sink that can prove the value is already at its destination may rank against the maximum;
    /// the connectors express that as a delivery rule.
    /// </para>
    /// </remarks>
    internal long LastSourceCommitRevision;

    /// <summary>
    /// Whether a sink has published this property's value. Sticky. Volatile because a stale read of
    /// false skips a transaction confirmation write-back, leaving the divergence it exists to repair.
    /// </summary>
    internal volatile bool Published;
}
