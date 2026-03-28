namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// Provides additional tools beyond the 4 core tools.
/// </summary>
public interface IMcpToolProvider
{
    /// <summary>
    /// Gets the additional tool descriptors.
    /// </summary>
    /// <returns>An enumerable of tool descriptors.</returns>
    IEnumerable<McpToolDescriptor> GetTools();
}
