using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Represents an OPC UA server that exposes a subject's properties as OPC UA nodes.
/// </summary>
/// <remarks>
/// The interface inherits <see cref="IHostedService"/> so non-DI consumers (see <c>CreateOpcUaServer</c>)
/// can host the server themselves. When the server is retrieved from DI via the registration handle,
/// the host owns the lifecycle: do not call <see cref="IHostedService.StartAsync"/> or
/// <see cref="IHostedService.StopAsync"/>.
/// </remarks>
public interface IOpcUaSubjectServer : IHostedService
{
    /// <summary>
    /// Gets diagnostic information about the server state.
    /// </summary>
    OpcUaServerDiagnostics Diagnostics { get; }

    /// <summary>
    /// Gets the underlying OPC UA <see cref="StandardServer"/>, or <c>null</c> if the server is not currently running.
    /// Intended for advanced scenarios that the connector does not cover natively, such as registering custom node managers,
    /// raising server events, or wiring up custom session handlers.
    /// </summary>
    /// <remarks>
    /// Lifecycle contract: read this property immediately before each use. The instance is recreated on every server restart
    /// (after a startup failure, force-kill, or any other lifecycle reset). Never cache the reference; never hold long-lived
    /// state keyed on a specific server instance.
    /// </remarks>
    StandardServer? CurrentServer { get; }

    /// <summary>
    /// Tries to get the OPC UA <see cref="BaseDataVariableState"/> node that this server has created
    /// for the given property. Returns <c>false</c> when the property is not exposed by this server,
    /// has not yet been created, or was removed during a server restart.
    /// </summary>
    /// <remarks>
    /// Useful for raising server-side events on a node that corresponds to a tracked property,
    /// or for inspecting node-level metadata the connector does not surface natively.
    /// </remarks>
    bool TryGetVariableNode(PropertyReference property, [NotNullWhen(true)] out BaseDataVariableState? variable);
}
