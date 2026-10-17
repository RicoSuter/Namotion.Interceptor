namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Hints that describe a tool's behavior to MCP clients, which may use them to decide which calls need the user's
/// approval. They are hints, not enforcement. A null hint is left out, so the client assumes the MCP default.
/// </summary>
public sealed record McpToolAnnotations
{
    /// <summary>A tool that only reads, within the server's own data.</summary>
    public static McpToolAnnotations ReadOnly { get; } = new() { ReadOnlyHint = true, OpenWorldHint = false };

    /// <summary>A tool that can change or destroy state, within the server's own data.</summary>
    public static McpToolAnnotations Destructive { get; } = new() { ReadOnlyHint = false, DestructiveHint = true, OpenWorldHint = false };

    /// <summary>Whether the tool does not modify its environment (MCP default: false).</summary>
    public bool? ReadOnlyHint { get; init; }

    /// <summary>Whether a modifying tool can perform destructive updates (MCP default: true).</summary>
    public bool? DestructiveHint { get; init; }

    /// <summary>Whether repeated calls with the same arguments have no additional effect (MCP default: false).</summary>
    public bool? IdempotentHint { get; init; }

    /// <summary>Whether the tool interacts with an open world of external entities (MCP default: true).</summary>
    public bool? OpenWorldHint { get; init; }
}
