using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Creates the graph tools agents may use.
/// </summary>
public interface IAgentToolSource
{
    /// <summary>
    /// Creates all graph tools; <paramref name="isReadOnly"/> blocks set_property and operations.
    /// </summary>
    IReadOnlyList<McpToolInfo> CreateTools(bool isReadOnly);
}
