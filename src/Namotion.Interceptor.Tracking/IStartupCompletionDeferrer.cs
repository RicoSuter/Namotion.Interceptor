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
/// Both halves of an implementation can run under the lifecycle lock. Namotion.Interceptor.Hosting calls
/// <see cref="DeferCompletion"/> synchronously from a lifecycle event, which fires inside
/// <see cref="Lifecycle.LifecycleInterceptor"/>'s attach lock during a property write, and it disposes
/// the returned hold from that same place when the start it was taken for is refused. An implementation
/// must therefore not block in either method on anything that can be waiting for the lifecycle lock, and
/// must not take a lock that a thread already holding the lifecycle lock can be waiting for. Either one
/// closes a cycle that nothing resolves: a caller holds the implementation's lock and waits for a hosted
/// service transition, that transition writes a subject typed property and so needs the lifecycle lock,
/// and the thread holding the lifecycle lock is inside <see cref="DeferCompletion"/> waiting for the
/// implementation's lock. The lifecycle lock is held throughout, so every structural property write in
/// the graph queues behind it rather than only the caller.
/// </para>
/// <para>
/// The take can avoid locking altogether: an interlocked increment returning a counted handle is enough,
/// which is what SourceMonitor in Namotion.Interceptor.Connectors does. A lock taken on the release path
/// is safe only when it is never held while waiting on anything that needs the lifecycle lock, so that
/// the two are always acquired in the same order.
/// </para>
/// </remarks>
public interface IStartupCompletionDeferrer
{
    /// <summary>
    /// Holds completion open until the returned handle is disposed.
    /// </summary>
    /// <remarks>
    /// This method and the returned handle's dispose can both run under the lifecycle lock, so neither
    /// may block on anything that needs it. See the remarks on <see cref="IStartupCompletionDeferrer"/>.
    /// </remarks>
    IDisposable DeferCompletion();
}
