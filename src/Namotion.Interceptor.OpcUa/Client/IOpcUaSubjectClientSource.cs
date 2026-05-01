using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Represents an OPC UA client source that synchronizes a subject's properties with an OPC UA server.
/// </summary>
/// <remarks>
/// The interface inherits <see cref="IHostedService"/> so non-DI consumers (see <c>CreateOpcUaClientSource</c>)
/// can host the source themselves. When the source is retrieved from DI via the registration handle,
/// the host owns the lifecycle: do not call <see cref="IHostedService.StartAsync"/> or
/// <see cref="IHostedService.StopAsync"/>.
/// </remarks>
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
    /// transitions to and from <c>null</c> during reconnection. The event args carry both the
    /// previous and the new session, so handlers can release state bound to the old session
    /// before recreating it on the new one.
    /// </summary>
    /// <remarks>
    /// Primary use case: long-running session-bound consumers (e.g. an Alarms &amp; Conditions
    /// <c>Subscription</c> created on the session) need to recreate their session-scoped state
    /// when the session is swapped, since they have no inbound traffic that would otherwise
    /// surface a stale session as a failure.
    ///
    /// The previous session's transport is still open while the handler runs; the connector closes
    /// it after the handler returns. Synchronous local cleanup on <see cref="OpcUaCurrentSessionChangedEventArgs.PreviousSession"/>
    /// is safe; new network operations on it will fail once the transport closes.
    ///
    /// Raised synchronously on the connector's own thread, often from inside the reconnection
    /// lock. Handlers must be fast and non-blocking; a slow handler stalls reconnection. For async
    /// work (e.g. recreating an A&amp;C subscription on the new session) use a fire-and-forget pattern
    /// (<c>_ = Task.Run(...)</c>) inside the handler and tolerate the new session being swapped again
    /// before the task completes. The connector wraps the invocation in a try/catch so a throwing
    /// handler cannot break its own state, but per standard .NET event semantics a throwing
    /// subscriber will skip subsequent subscribers in the invocation list.
    /// </remarks>
    event EventHandler<OpcUaCurrentSessionChangedEventArgs>? CurrentSessionChanged;

    /// <summary>
    /// Tries to get the OPC UA <see cref="NodeId"/> that this source has bound to the given property.
    /// Returns <c>false</c> when the property is not owned by this source, has not yet been resolved,
    /// or was unbound during reconnection.
    /// </summary>
    /// <remarks>
    /// Useful when raw <see cref="ISession"/> calls need a NodeId for a property the consumer already
    /// holds (e.g. invoking an OPC UA Method whose input is the value of a tracked property).
    /// </remarks>
    bool TryGetNodeId(PropertyReference property, [NotNullWhen(true)] out NodeId? nodeId);
}
