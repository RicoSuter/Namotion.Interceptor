using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Connects the MCP tools to the <see cref="AuthorizationService"/>.
/// </summary>
internal static class McpAuthorization
{
    /// <summary>The role names of a client that may do everything the system rules allow.</summary>
    public static IReadOnlyCollection<string> EverythingRoleNames { get; } = [BuiltInRoles.EverythingName];

    /// <summary>
    /// The path provider the authorization matches rule paths with: the paths the tools build, and a path for every
    /// attribute and metadata member that does not depend on its value.
    /// </summary>
    public static RulePathProvider PathProvider { get; } = new();

    /// <summary>
    /// Gets the access policy of a client holding <paramref name="roleNames"/> from <paramref name="authorizationService"/>;
    /// <see cref="AuthorizationPolicy.DenyAll"/> when the roles do not compile.
    /// </summary>
    public static AuthorizationMcpAccessPolicy GetAccessPolicy(AuthorizationService authorizationService, IReadOnlyCollection<string> roleNames)
    {
        var policy = authorizationService.GetPolicy(roleNames).Policy;
        return new(() => policy);
    }
}
