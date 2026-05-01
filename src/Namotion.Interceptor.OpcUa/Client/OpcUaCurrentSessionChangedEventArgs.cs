using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Event data for <see cref="IOpcUaSubjectClientSource.CurrentSessionChanged"/>. Carries both the
/// previous and the new session so consumers can release session-bound state on the old session
/// before recreating it on the new one. Either side may be <c>null</c> during connect, disconnect
/// or reconnection transitions.
/// </summary>
public sealed class OpcUaCurrentSessionChangedEventArgs : EventArgs
{
    public OpcUaCurrentSessionChangedEventArgs(ISession? previousSession, ISession? currentSession)
    {
        PreviousSession = previousSession;
        CurrentSession = currentSession;
    }

    /// <summary>
    /// Gets the session that was active before this transition, or <c>null</c> if no session was active.
    /// The transport is still open while the handler runs; the connector closes and disposes it after the
    /// handler returns. Use <c>PreviousSession</c> only for synchronous local cleanup (unsubscribing event
    /// handlers, dropping references, etc.). Do not start new operations on it — outstanding async work
    /// will fail when the transport is closed.
    /// </summary>
    public ISession? PreviousSession { get; }

    /// <summary>
    /// Gets the session that is now active, or <c>null</c> if the connection is currently down.
    /// </summary>
    public ISession? CurrentSession { get; }
}
