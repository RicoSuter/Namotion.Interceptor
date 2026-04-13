using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Represents an OPC UA server that exposes a subject's properties as OPC UA nodes.
/// </summary>
public interface IOpcUaSubjectServer : IHostedService
{
    /// <summary>
    /// Gets diagnostic information about the server state.
    /// </summary>
    OpcUaServerDiagnostics Diagnostics { get; }
}
