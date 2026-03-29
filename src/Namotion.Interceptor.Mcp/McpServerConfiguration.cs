using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Configuration for the MCP subject server.
/// </summary>
public class McpServerConfiguration
{
    /// <summary>
    /// Property filtering and path resolution. Reuses existing IPathProvider from Registry.
    /// </summary>
    public required IPathProvider PathProvider { get; init; }

    /// <summary>
    /// Subject-level JSON enrichment for query responses (e.g., $title, $icon, $type).
    /// </summary>
    public IList<IMcpSubjectEnricher> SubjectEnrichers { get; init; } = [];

    /// <summary>
    /// Type discovery for the list_types tool.
    /// </summary>
    public IList<IMcpTypeProvider> TypeProviders { get; init; } = [];

    /// <summary>
    /// Additional tools beyond the 5 core tools (e.g., list_methods, invoke_method).
    /// </summary>
    public IList<IMcpToolProvider> ToolProviders { get; init; } = [];

    /// <summary>
    /// Maximum subject tree traversal depth (default: 10).
    /// </summary>
    public int MaxDepth { get; init; } = 10;

    /// <summary>
    /// Server-side maximum subjects per response (default: 500).
    /// Agents can request a lower limit via the maxSubjects parameter.
    /// </summary>
    public int MaxSubjectsPerResponse { get; init; } = 500;

    /// <summary>
    /// When true, set_property is blocked and invoke_method only allows Query methods.
    /// </summary>
    public bool IsReadOnly { get; init; } = true;
}
