using System.Collections.Immutable;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authorization;

/// <summary>
/// The deny rules compiled into every policy, whatever the roles allow.
/// </summary>
public static class SystemRules
{
    /// <summary>
    /// The full name of the base type of agents, named here because this project does not reference it. A type rule
    /// matches the type and every type derived from it.
    /// </summary>
    public const string LlmAgentBaseTypeName = "HomeBlaze.AI.Agents.LlmAgentBase";

    /// <summary>
    /// Denies reading, writing and the history of secrets, writing configuration, executing operations that require
    /// confirmation, and every action on agents and on the authorization and its roles.
    /// </summary>
    public static ImmutableArray<PermissionRule> Rules { get; } =
    [
        new PermissionRule { Tags = [MemberTags.Secret], Actions = [PermissionAction.Read, PermissionAction.Write, AuthorizationActions.History] },
        new PermissionRule { Tags = [MemberTags.Configuration], Actions = [PermissionAction.Write] },
        new PermissionRule { Tags = [MemberTags.RequiresConfirmation], Actions = [AuthorizationActions.Execute] },
        new PermissionRule { Types = [LlmAgentBaseTypeName], Actions = AuthorizationActions.All },
        new PermissionRule { Types = [typeof(Authorization).FullName!, typeof(Role).FullName!], Actions = AuthorizationActions.All }
    ];
}
