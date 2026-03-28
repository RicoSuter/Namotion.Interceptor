using System.Text.Json;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Transport-agnostic tool descriptor. Consumers wrap as MCP tools or AIFunction.
/// </summary>
public class McpToolDescriptor
{
    /// <summary>
    /// Gets the tool name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the tool description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the JSON schema describing the tool's input parameters.
    /// </summary>
    public required JsonElement InputSchema { get; init; }

    /// <summary>
    /// Gets the handler function that executes the tool.
    /// </summary>
    public required Func<JsonElement, CancellationToken, Task<object?>> Handler { get; init; }
}
