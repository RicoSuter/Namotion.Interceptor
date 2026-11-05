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
    /// Property filtering and path resolution. The tools list, filter on, resolve and build paths only through the
    /// properties it effectively includes (see <see cref="PathProviderExtensions.IsPropertyEffectivelyIncluded"/>).
    /// </summary>
    public required PathProviderBase PathProvider { get; init; }

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
    /// Decides per member and action what the tools let a client read and change, see "Access Policy" in the MCP documentation. A
    /// member the policy does not allow to read reads as missing, and set_property refuses a member it does not allow
    /// to write. Defaults to <see cref="ReadOnlyMcpAccessPolicy.Instance"/>, which allows reading only; set
    /// <see cref="AllowAllMcpAccessPolicy.Instance"/> to allow every action.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public IMcpAccessPolicy AccessPolicy
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    } = ReadOnlyMcpAccessPolicy.Instance;

    /// <summary>
    /// The policy the read checks of the tools ask, or null when <see cref="AccessPolicy"/> allows every read, so that
    /// they evaluate and allocate nothing.
    /// </summary>
    internal IMcpAccessPolicy? ReadPolicy =>
        AccessPolicy is ReadOnlyMcpAccessPolicy or AllowAllMcpAccessPolicy ? null : AccessPolicy;

    private PathProviderBase? _toolPathProvider;

    /// <summary>
    /// The path provider the tools list, select, filter and resolve through and build the paths they return with:
    /// <see cref="PathProvider"/> restricted to what <see cref="AccessPolicy"/> allows to read, or
    /// <see cref="PathProvider"/> itself without a policy.
    /// </summary>
    // Racing first calls create equal providers, so whichever one is kept is correct.
    internal PathProviderBase ToolPathProvider => _toolPathProvider ??= ReadPolicy is { } policy
        ? new McpAccessPathProvider(PathProvider, policy)
        : PathProvider;
}
