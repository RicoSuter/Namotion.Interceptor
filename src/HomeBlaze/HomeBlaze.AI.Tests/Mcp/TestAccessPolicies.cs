using HomeBlaze.AI.Mcp;
using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.AI.Tests.Mcp;

/// <summary>Access policies compiled as the authorization service compiles them, with the system rules.</summary>
internal static class TestAccessPolicies
{
    /// <summary>The policy of the external MCP server.</summary>
    public static AuthorizationMcpAccessPolicy Everything { get; } = Create(BuiltInRoles.EverythingName);

    /// <summary>The policy of agents today.</summary>
    public static AuthorizationMcpAccessPolicy Operator { get; } = Create(BuiltInRoles.OperatorName);

    /// <summary>Compiles the policy of the built-in roles and <paramref name="customRoles"/> for <paramref name="roleName"/>.</summary>
    public static AuthorizationMcpAccessPolicy Create(string roleName, params AuthorizationRole[] customRoles) =>
        new(AuthorizationPolicy.Compile(
            [.. BuiltInRoles.All, .. customRoles],
            [roleName],
            SystemRules.Rules,
            AuthorizationActions.All,
            new HomeBlazeMemberTagProvider(),
            new StateAttributePathProvider()));
}
