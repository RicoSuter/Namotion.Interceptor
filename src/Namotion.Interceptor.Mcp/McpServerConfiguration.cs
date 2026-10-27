using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
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
    /// Subject types, and the types derived from them, to exclude from browse and search results and from list_types.
    /// </summary>
    public Type[] ExcludeTypes { get; init; } = [];

    /// <summary>
    /// Decides per member and action what the tools show and change, see "Access Policy" in the MCP documentation. A
    /// member the policy does not allow to read reads as missing, and set_property refuses a member it does not allow
    /// to write. Null (the default) allows every action. Applies in addition to <see cref="IsReadOnly"/>.
    /// </summary>
    public IMcpAccessPolicy? AccessPolicy { get; init; }

    /// <summary>
    /// When true, set_property is blocked.
    /// </summary>
    public bool IsReadOnly { get; init; } = true;

    private PathProviderBase? _toolPathProvider;
    private PathProviderBase? _resolutionPathProvider;

    /// <summary>
    /// The path provider the tools list, select and filter through: <see cref="PathProvider"/>, which must extend
    /// <see cref="PathProviderBase"/>, restricted to what <see cref="AccessPolicy"/> allows to read.
    /// </summary>
    // Racing first calls create equal providers, so whichever one is kept is correct.
    internal PathProviderBase ToolPathProvider => _toolPathProvider ??= McpAccessPathProvider.Create(PathProvider, AccessPolicy, appliesInnerInclusion: true);

    /// <summary>
    /// The path provider a requested path resolves through: with an <see cref="AccessPolicy"/>, it includes exactly
    /// what the policy allows to read; without one it is <see cref="PathProvider"/>, whose inclusion resolution does
    /// not apply.
    /// </summary>
    internal PathProviderBase ResolutionPathProvider => _resolutionPathProvider ??= McpAccessPathProvider.Create(PathProvider, AccessPolicy, appliesInnerInclusion: false);
}
