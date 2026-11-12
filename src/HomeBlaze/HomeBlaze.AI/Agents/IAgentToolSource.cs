using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Creates the graph tools agents may use.
/// </summary>
public interface IAgentToolSource
{
    /// <summary>
    /// Creates all graph tools; <paramref name="accessPolicy"/> decides per member and action what is readable for the
    /// tools and what they may change.
    /// </summary>
    IReadOnlyList<McpToolInfo> CreateTools(IMcpAccessPolicy accessPolicy);
}
