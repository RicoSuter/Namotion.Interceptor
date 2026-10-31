using HomeBlaze.Authorization;
using Microsoft.Extensions.DependencyInjection;
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
    /// The path provider the authorization matches rule paths with: one like that of the tools, so that a rule path
    /// matches the paths the tools show.
    /// </summary>
    public static StateAttributePathProvider PathProvider { get; } = new();

    /// <summary>
    /// Gets the access policy of a client holding <paramref name="roleNames"/> from the <see cref="AuthorizationService"/>;
    /// <see cref="AuthorizationPolicy.DenyAll"/> when the roles do not compile.
    /// </summary>
    public static AuthorizationMcpAccessPolicy GetAccessPolicy(IServiceProvider serviceProvider, IReadOnlyCollection<string> roleNames)
        => GetAccessPolicy(serviceProvider.GetRequiredService<AuthorizationService>(), roleNames);

    /// <summary>
    /// Gets the access policy of a client holding <paramref name="roleNames"/> from <paramref name="authorizationService"/>;
    /// <see cref="AuthorizationPolicy.DenyAll"/> when the roles do not compile.
    /// </summary>
    public static AuthorizationMcpAccessPolicy GetAccessPolicy(AuthorizationService authorizationService, IReadOnlyCollection<string> roleNames)
        => new(authorizationService.GetPolicy(roleNames).Policy);
}
