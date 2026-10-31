using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Creates the graph tools agents may use.
/// </summary>
public interface IAgentToolSource
{
    /// <summary>
    /// Creates all graph tools; <paramref name="isReadOnly"/> blocks set_property and operations, and
    /// <paramref name="accessPolicy"/> decides per member and action what the tools show and change.
    /// </summary>
    IReadOnlyList<McpToolInfo> CreateTools(bool isReadOnly, IMcpAccessPolicy accessPolicy);
}
