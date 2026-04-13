using Microsoft.Extensions.Hosting;

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
}
