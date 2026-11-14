using Microsoft.Extensions.AI;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Extension methods for <see cref="McpToolInfo"/>.
/// </summary>
public static class McpToolInfoExtensions
{
    /// <summary>
    /// Wraps the tool as an <see cref="AIFunction"/> so in-process chat clients can call it without the MCP protocol.
    /// </summary>
    /// <param name="tool">The tool to wrap.</param>
    /// <returns>A function whose name, description and schema are the tool's, and whose invocation calls the tool's handler.</returns>
    public static AIFunction ToAIFunction(this McpToolInfo tool) => new McpToolAIFunction(tool);
}
