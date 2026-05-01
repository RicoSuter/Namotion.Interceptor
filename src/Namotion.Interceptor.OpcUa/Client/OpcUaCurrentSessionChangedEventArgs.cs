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
    /// May already be disposed by the time the handler runs.
    /// </summary>
    public ISession? PreviousSession { get; }

    /// <summary>
    /// Gets the session that is now active, or <c>null</c> if the connection is currently down.
    /// </summary>
    public ISession? CurrentSession { get; }
}
