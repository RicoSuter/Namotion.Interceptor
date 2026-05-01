using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Event data for <see cref="IOpcUaSubjectClientSource.CurrentSessionChanged"/>. Either side may be
/// <c>null</c> during connect, disconnect or reconnection transitions.
/// </summary>
public sealed class OpcUaCurrentSessionChangedEventArgs : EventArgs
{
    public OpcUaCurrentSessionChangedEventArgs(ISession? previousSession, ISession? currentSession)
    {
        PreviousSession = previousSession;
        CurrentSession = currentSession;
    }

    /// <summary>
    /// Gets the session that was active before this transition, or <c>null</c> if none was active. Use
    /// only for synchronous local cleanup (drop refs, unsubscribe handlers); transport may already be
    /// closed, so do not start new network operations on it.
    /// </summary>
    public ISession? PreviousSession { get; }

    /// <summary>
    /// Gets the session that is now active, or <c>null</c> if the connection is currently down.
    /// </summary>
    public ISession? CurrentSession { get; }
}
