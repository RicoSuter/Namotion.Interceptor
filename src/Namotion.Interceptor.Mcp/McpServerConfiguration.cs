using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
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
    /// Additional tools beyond the 5 core tools, for example tools that call methods of subjects.
    /// </summary>
    public IList<IMcpToolProvider> ToolProviders { get; init; } = [];

    /// <summary>
    /// Text appended to the description of a tool, keyed by tool name, separated by a space. Lets an application
    /// describe its own conventions, for example which subjects hold its documentation, while keeping the generic
    /// description. Names of tools that are not offered are ignored.
    /// </summary>
    public IDictionary<string, string> ToolDescriptionAdditions { get; init; } = new Dictionary<string, string>();

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
    /// Prefix prepended to all output paths. Default is empty.
    /// Set to "/" for slash-based paths that should be absolute.
    /// </summary>
    public string PathPrefix { get; init; } = "";

    /// <summary>
    /// Subject types to exclude from browse and search results.
    /// </summary>
    public Type[] ExcludeTypes { get; init; } = [];

    /// <summary>
    /// Decides whether set_property may write a property. Returns null to allow the write, or the error message
    /// returned to the client, in which case nothing is written. Decide on the property; the path is only for the
    /// message: the client's text for get_property and set_property, the path the subject node shows for browse and
    /// search.
    /// Null (the default) allows every writable property. Not consulted when <see cref="IsReadOnly"/> is true.
    /// Also decides whether get_property, browse and search report a property as writable. May be called
    /// concurrently, so it must be thread-safe.
    /// </summary>
    public Func<RegisteredSubjectProperty, string, string?>? PropertyWriteFilter { get; init; }

    /// <summary>
    /// When true, set_property is blocked.
    /// </summary>
    public bool IsReadOnly { get; init; } = true;
}
