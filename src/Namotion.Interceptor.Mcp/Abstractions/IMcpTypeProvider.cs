namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// Provides type information for the list_types tool.
/// </summary>
public interface IMcpTypeProvider
{
    /// <summary>
    /// Gets the available types in the subject registry.
    /// </summary>
    /// <returns>An enumerable of type information descriptors.</returns>
    IEnumerable<McpTypeInfo> GetTypes();
}
