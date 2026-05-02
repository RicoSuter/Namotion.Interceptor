using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// OPC UA client source that synchronizes a subject's properties with an OPC UA server.
/// Inherits <see cref="IHostedService"/> for non-DI hosting; when resolved from DI the host owns the lifecycle.
/// </summary>
public interface IOpcUaSubjectClientSource : IHostedService
{
    /// <summary>
    /// Gets diagnostic information about the client connection state.
    /// </summary>
    OpcUaClientDiagnostics Diagnostics { get; }

    /// <summary>
    /// Gets the current OPC UA session, or <c>null</c> if none is established. Intended for scenarios
    /// the connector does not cover natively (Methods, Alarms &amp; Conditions).
    /// </summary>
    /// <remarks>
    /// Read immediately before each use. The session can be null and the instance can change
    /// (manual reconnect, transferred-subscription failure, stall reset). Do not cache the reference.
    /// Subscribe to <see cref="CurrentSessionChanged"/> to react to swaps.
    /// </remarks>
    ISession? CurrentSession { get; }

    /// <summary>
    /// Raised on every transition of <see cref="CurrentSession"/> (including to/from <c>null</c>) so
    /// handlers can release state bound to the old session and recreate it on the new one. Mainly for
    /// long-running session-bound consumers (e.g. an A&amp;C <c>Subscription</c>) that have no inbound
    /// traffic to surface a stale session as a failure.
    /// </summary>
    /// <remarks>
    /// Use <see cref="OpcUaCurrentSessionChangedEventArgs.PreviousSession"/> only for synchronous local
    /// cleanup (dropping refs, unsubscribing handlers). Its transport may already be closed; do not start
    /// new network operations on it. Fired outside the reconnection lock, in transition order. For async
    /// work on the new session use fire-and-forget (<c>_ = Task.Run(...)</c>) and tolerate another swap
    /// before it completes. Handler exceptions are caught and logged, but per standard event semantics a
    /// throwing subscriber skips later subscribers.
    /// </remarks>
    event EventHandler<OpcUaCurrentSessionChangedEventArgs>? CurrentSessionChanged;

    /// <summary>
    /// Tries to get the OPC UA <see cref="NodeId"/> bound to <paramref name="property"/>. Returns <c>false</c>
    /// if the property is not owned by this source or not yet resolved.
    /// Useful when a raw <see cref="ISession"/> call needs the NodeId of a tracked property.
    /// </summary>
    bool TryGetNodeId(PropertyReference property, [NotNullWhen(true)] out NodeId? nodeId);
}
