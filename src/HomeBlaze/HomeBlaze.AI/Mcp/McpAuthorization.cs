using HomeBlaze.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Connects the MCP tools to the <see cref="AuthorizationService"/>.
/// </summary>
internal static class McpAuthorization
{
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
        => new(serviceProvider.GetRequiredService<AuthorizationService>().GetPolicy(roleNames).Policy);
}
