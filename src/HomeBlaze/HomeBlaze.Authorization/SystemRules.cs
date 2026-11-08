using System.Collections.Immutable;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authorization;

/// <summary>
/// The deny rules compiled into every policy, whatever the roles allow.
/// </summary>
public static class SystemRules
{
    // The full name of the base type of agents, named here because this project does not reference it. A type rule
    // matches the type and every type derived from it.
    private const string LlmAgentBaseTypeName = "HomeBlaze.AI.Agents.LlmAgentBase";

    private static readonly PermissionRule ConfigurationWrites =
        new() { Tags = [MemberTags.Configuration], Actions = [PermissionAction.Write] };

    private static readonly PermissionRule AdministrationExecutes =
        new() { Tags = [MemberTags.Administration], Actions = [AuthorizationActions.Execute] };

    private static readonly ImmutableArray<PermissionRule> Common =
    [
        new PermissionRule { Tags = [MemberTags.Secret], Actions = [PermissionAction.Read, AuthorizationActions.History] },
        ConfigurationWrites,
        AdministrationExecutes
    ];

    private static readonly ImmutableArray<PermissionRule> NonHuman =
    [
        new PermissionRule { Tags = [MemberTags.Secret], Actions = [PermissionAction.Read, PermissionAction.Write, AuthorizationActions.History] },
        ConfigurationWrites,
        AdministrationExecutes,
        new PermissionRule { Tags = [MemberTags.RequiresConfirmation], Actions = [AuthorizationActions.Execute] },
        new PermissionRule { Actions = [AuthorizationActions.Administer] },
        new PermissionRule { Types = [LlmAgentBaseTypeName], Actions = AuthorizationActions.All },
        new PermissionRule { Types = [typeof(Authorization).FullName!, typeof(Role).FullName!, typeof(User).FullName!], Actions = AuthorizationActions.All }
    ];

    private static readonly ImmutableArray<PermissionRule> Person =
    [
        .. Common,
        new PermissionRule { Types = [typeof(Authorization).FullName!, typeof(Role).FullName!], Actions = [PermissionAction.Write, AuthorizationActions.Execute] },
        new PermissionRule { Types = [typeof(User).FullName!], Actions = [PermissionAction.Read, PermissionAction.Write, AuthorizationActions.Execute, AuthorizationActions.History] }
    ];

    private static readonly ImmutableArray<PermissionRule> Anonymous =
    [
        .. Person,
        new PermissionRule { Actions = [AuthorizationActions.Administer] }
    ];

    /// <summary>
    /// Gets the deny rules compiled into every policy of <paramref name="kind"/>, whatever the roles allow. Every kind:
    /// secrets are never read nor their history, configuration is never written and administration operations never
    /// executed. Agents and MCP clients additionally never write secrets, execute operations that require confirmation,
    /// administer, or touch agents, the authorization, roles and users. People never write or execute on the
    /// authorization and roles and never touch users through these actions; visitors who are not signed in also never
    /// administer.
    /// </summary>
    public static ImmutableArray<PermissionRule> For(PrincipalKind kind) => kind switch
    {
        PrincipalKind.NonHuman => NonHuman,
        PrincipalKind.Person => Person,
        PrincipalKind.Anonymous => Anonymous,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
