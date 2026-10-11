using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Transport-agnostic tool descriptor. Consumers wrap as MCP tools or AIFunction.
/// </summary>
public class McpToolInfo
{
    /// <summary>
    /// Initializes a new instance; all required members must be set.
    /// </summary>
    public McpToolInfo()
    {
    }

    /// <summary>
    /// Initializes a copy of <paramref name="source"/>. An object initializer can then replace single members,
    /// for example <c>new McpToolInfo(tool) { Handler = wrappedHandler }</c>.
    /// </summary>
    [SetsRequiredMembers]
    public McpToolInfo(McpToolInfo source)
    {
        Name = source.Name;
        Description = source.Description;
        InputSchema = source.InputSchema;
        Handler = source.Handler;
        Annotations = source.Annotations;
    }

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

    /// <summary>
    /// Gets the hints that describe the tool's behavior to MCP clients, or null for none.
    /// </summary>
    public McpToolAnnotations? Annotations { get; init; }
}
