using System.Collections.Immutable;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// A named set of allow and deny rules that can include other roles. The effective rules of a role are its own and
/// those of every role it includes, transitively.
/// </summary>
public sealed record AuthorizationRole
{
    /// <summary>Gets the name, unique among the roles of a policy ignoring case. <c>AuthorizationPolicy.SystemRoleName</c> is reserved.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the names of the included roles.</summary>
    public ImmutableArray<string> Includes { get; init; } = [];

    /// <summary>Gets the rules that grant.</summary>
    public ImmutableArray<PermissionRule> Allow { get; init; } = [];

    /// <summary>Gets the rules that remove what any allow rule granted.</summary>
    public ImmutableArray<PermissionRule> Deny { get; init; } = [];
}
