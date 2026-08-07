namespace Namotion.Interceptor.Tracking;

/// <summary>
/// A service whose "everything has started" signal can be held open while another subsystem still
/// has queued work that has not run yet.
/// </summary>
/// <remarks>
/// Exists so a subsystem that defers work can say so without the two sides referencing each other.
/// Concretely: Namotion.Interceptor.Hosting starts a hosted service attached to the subject graph by
/// queueing it, not by running it inline, so the service is not started when the attach returns.
/// Namotion.Interceptor.Connectors treats "every source has registered" as the point where
/// synchronization waits may complete, and would otherwise reach that point while a queued source
/// start is still on its way in, completing a wait against a tree that is not synchronized. Those
/// two packages are siblings and neither references the other, so the handshake lives here, in the
/// package they both build on.
/// <para>
/// A holder takes a hold before queueing the work and disposes it once the work has run, including
/// when the work throws. Holds are counted, so concurrent holders compose. Taking a hold never
/// un-completes a signal that has already fired; it only blocks one that has not.
/// </para>
/// </remarks>
public interface IStartupCompletionDeferrer
{
    /// <summary>
    /// Holds completion open until the returned handle is disposed.
    /// </summary>
    IDisposable DeferCompletion();
}
