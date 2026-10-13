namespace Namotion.Interceptor.Tracking;

/// <summary>
/// A point at which startup counts as complete, which other subsystems can defer while work that
/// should count towards it is still outstanding.
/// </summary>
/// <remarks>
/// Exists so two packages that do not reference each other can hand off. Namotion.Interceptor.Hosting
/// queues an attached hosted service's start rather than running it inline, and
/// Namotion.Interceptor.Connectors treats "every source has registered" as the point where waits may
/// complete; without this it would reach that point while a queued start was still pending. A
/// deferral taken for a start covers the service's <c>StartAsync</c> returning and nothing after it;
/// what a <c>BackgroundService</c> runs after that is outside it, so a service that registers a source
/// must do so in <c>StartAsync</c>.
/// <para>
/// Defer before queueing the work and dispose the returned handle once it has run, including on
/// failure. Deferrals are counted. Deferring never reopens a wait that has already completed.
/// </para>
/// <para>
/// <b>The constraint on an implementation.</b> Both <see cref="Defer"/> and disposing the handle it
/// returns can run under the lifecycle lock, because Namotion.Interceptor.Hosting defers startup
/// completion from inside a lifecycle event and releases the deferral from that same place when the
/// start it was taken for is refused. So neither may block on anything that needs that lock, and a
/// lock of your own is allowed only where its order against the lifecycle lock is already fixed. An
/// interlocked counter needs no lock at all, which is what SourceMonitor in
/// Namotion.Interceptor.Connectors does. The cycle this avoids, and why its blast radius is every
/// structural property write in the graph rather than the caller alone, is worked through in
/// docs/design/hosting-service-ownership.md#4-a-startup-completion-that-takes-a-lock-of-its-own.
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
