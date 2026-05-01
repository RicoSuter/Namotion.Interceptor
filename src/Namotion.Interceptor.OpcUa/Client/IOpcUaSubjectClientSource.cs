using Microsoft.Extensions.Hosting;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Represents an OPC UA client source that synchronizes a subject's properties with an OPC UA server.
/// </summary>
public interface IOpcUaSubjectClientSource : IHostedService
{
    /// <summary>
    /// Gets diagnostic information about the client connection state.
    /// </summary>
    OpcUaClientDiagnostics Diagnostics { get; }

    /// <summary>
    /// Gets the current OPC UA session, or <c>null</c> if no session is currently established.
    /// Intended for advanced scenarios that the connector does not cover natively, such as
    /// calling OPC UA Methods or subscribing to Alarms &amp; Conditions events.
    /// </summary>
    /// <remarks>
    /// Lifecycle contract: read this property immediately before each use. The returned session
    /// can become null at any time (during reconnection) and the underlying instance can change
    /// between reads (after a manual reconnect, transferred-subscription failure, or stall reset).
    /// Do not cache the reference; do not hold long-lived state keyed on a specific session
    /// instance. Callers are responsible for handling transient nulls and session swaps.
    /// Subscribe to <see cref="CurrentSessionChanged"/> to react to session swaps.
    /// </remarks>
    ISession? CurrentSession { get; }

    /// <summary>
    /// Raised whenever <see cref="CurrentSession"/> transitions to a new value, including
    /// transitions to and from <c>null</c> during reconnection. The argument is the new session
    /// (which may be <c>null</c>).
    /// </summary>
    /// <remarks>
    /// Primary use case: long-running session-bound consumers (e.g. an Alarms &amp; Conditions
    /// <c>Subscription</c> created on the session) need to recreate their session-scoped state
    /// when the session is swapped, since they have no inbound traffic that would otherwise
    /// surface a stale session as a failure.
    ///
    /// Handlers should be fast and non-blocking; the event is raised on the connector's own
    /// thread, including from inside reconnection paths. Exceptions thrown by handlers are
    /// caught and logged so a misbehaving handler does not destabilize the connector.
    /// </remarks>
    event Action<ISession?>? CurrentSessionChanged;
}
