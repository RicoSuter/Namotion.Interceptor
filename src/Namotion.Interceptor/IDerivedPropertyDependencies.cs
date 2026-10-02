namespace Namotion.Interceptor;

/// <summary>
/// The record of which properties a derived getter reads, kept per derived property by a tracker that
/// records the reads, and stored as property data under <see cref="PropertyReference.DerivedDependenciesKey"/>
/// so the paired value read finds it without a reference to the tracker.
/// </summary>
internal interface IDerivedPropertyDependencies
{
    /// <summary>
    /// Gets the latest write timestamp among the recorded dependencies, as raw UTC ticks, or 0 when none
    /// is recorded or none has been written.
    /// </summary>
    long GetLatestDependencyWriteTimestampTicks();
}
