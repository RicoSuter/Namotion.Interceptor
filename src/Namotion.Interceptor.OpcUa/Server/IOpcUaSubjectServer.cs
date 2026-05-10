using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// OPC UA server that exposes a subject's properties as OPC UA nodes.
/// Inherits <see cref="IHostedService"/> for non-DI hosting; when resolved from DI the host owns the lifecycle.
/// </summary>
public interface IOpcUaSubjectServer : IHostedService
{
    /// <summary>
    /// Gets diagnostic information about the server state.
    /// </summary>
    OpcUaServerDiagnostics Diagnostics { get; }

    /// <summary>
    /// Gets the underlying OPC UA <see cref="StandardServer"/>, or <c>null</c> if not running. Intended
    /// for scenarios the connector does not cover natively (custom node managers, server events).
    /// </summary>
    /// <remarks>
    /// Read immediately before each use. The instance is recreated on every restart (startup failure,
    /// force-kill, or any other lifecycle reset); do not cache the reference.
    /// </remarks>
    StandardServer? CurrentServer { get; }

    /// <summary>
    /// Tries to get the OPC UA <see cref="BaseDataVariableState"/> created for <paramref name="property"/>.
    /// Returns <c>false</c> if the property is not exposed by this server, not yet created, or was
    /// removed during a server restart. Useful for raising server-side events on a tracked property.
    /// </summary>
    bool TryGetVariableNode(PropertyReference property, [NotNullWhen(true)] out BaseDataVariableState? variable);
}
