namespace Namotion.Interceptor.Tracking;

/// <summary>
/// A point at which startup counts as complete, which other subsystems can defer while work that
/// should count towards it is still outstanding.
/// </summary>
/// <remarks>
/// Exists so two packages that do not reference each other can hand off. Namotion.Interceptor.Hosting
/// queues an attached hosted service's start rather than running it inline, and
/// Namotion.Interceptor.Connectors treats "every source has registered" as the point where waits may
/// complete; without this it would reach that point while a queued start was still pending.
/// <para>
/// Defer before queueing the work and dispose the returned handle once it has run, including on
/// failure. Deferrals are counted. Deferring never reopens a wait that has already completed.
/// </para>
/// </remarks>
public interface IStartupCompletion
{
    /// <summary>
    /// Defers startup completion until the returned handle is disposed. Deferrals are counted, and
    /// deferring never reopens a wait that has already completed.
    /// </summary>
    IDisposable Defer();
}
