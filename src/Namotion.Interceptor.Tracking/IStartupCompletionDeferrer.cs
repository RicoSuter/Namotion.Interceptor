namespace Namotion.Interceptor.Tracking;

/// <summary>
/// A service whose "everything has started" signal can be held open while another subsystem still
/// has queued work that has not run yet.
/// </summary>
/// <remarks>
/// Exists so two packages that do not reference each other can hand off. Namotion.Interceptor.Hosting
/// queues an attached hosted service's start rather than running it inline, and
/// Namotion.Interceptor.Connectors treats "every source has registered" as the point where waits may
/// complete; without this it would reach that point while a queued start was still pending.
/// <para>
/// Take a hold before queueing the work and dispose it once the work has run, including on failure.
/// Holds are counted. Taking one never un-completes a signal that has already fired.
/// </para>
/// <para>
/// <b>The constraint on an implementation.</b> Both methods can run under the lifecycle lock, because
/// Namotion.Interceptor.Hosting takes a hold from inside a lifecycle event and releases it from that
/// same place when the start it was taken for is refused. So neither may block on anything that needs
/// that lock, and a lock of your own is allowed only where its order against the lifecycle lock is
/// already fixed. An interlocked counter needs no lock at all, which is what SourceMonitor in
/// Namotion.Interceptor.Connectors does. The cycle this avoids, and why its blast radius is every
/// structural property write in the graph rather than the caller alone, is worked through in
/// docs/design/hosting-service-ownership.md#4-a-deferrer-that-takes-a-lock-of-its-own.
/// </para>
/// </remarks>
public interface IStartupCompletionDeferrer
{
    /// <summary>
    /// Holds completion open until the returned handle is disposed.
    /// </summary>
    IDisposable DeferCompletion();
}
