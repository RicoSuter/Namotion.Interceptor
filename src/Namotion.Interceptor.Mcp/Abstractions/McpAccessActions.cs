namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// The actions the tools ask an <see cref="IMcpAccessPolicy"/> about.
/// </summary>
public static class McpAccessActions
{
    /// <summary>Reading a member's value, and seeing that it exists.</summary>
    public const string Read = "read";

    /// <summary>Writing a member's value with set_property.</summary>
    public const string Write = "write";
}
