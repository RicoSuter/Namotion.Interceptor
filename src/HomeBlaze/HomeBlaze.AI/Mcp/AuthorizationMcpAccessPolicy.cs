using System.Collections.Frozen;
using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// An <see cref="AuthorizationPolicy"/> as the access policy of the MCP tools. The actions <c>read</c>, <c>write</c>,
/// <c>execute</c> and <c>history</c> are matched ignoring case; any other action is denied.
/// </summary>
public sealed class AuthorizationMcpAccessPolicy : IMcpAccessPolicy
{
    private static readonly FrozenDictionary<string, PermissionAction> Actions =
        AuthorizationActions.All.ToFrozenDictionary(action => action.Name, StringComparer.OrdinalIgnoreCase);

    /// <param name="policy">The policy that decides.</param>
    public AuthorizationMcpAccessPolicy(AuthorizationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
    }

    /// <summary>Gets the policy that decides.</summary>
    public AuthorizationPolicy Policy { get; }

    /// <inheritdoc />
    public bool IsAllowed(RegisteredSubjectProperty member, string action) =>
        Actions.TryGetValue(action, out var permissionAction) && Policy.IsAllowed(member, permissionAction);
}
