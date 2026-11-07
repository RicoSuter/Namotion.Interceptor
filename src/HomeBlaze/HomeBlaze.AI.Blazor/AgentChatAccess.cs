using HomeBlaze.AI.Agents;
using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;

namespace HomeBlaze.AI.Blazor;

/// <summary>
/// Decides who may chat with an agent. An agent shares one conversation, timeline and memory among everyone who chats
/// with it, so a person may chat only when they may act through the roles of the agent
/// (<see cref="IUserPermissions.CanActThrough"/>): the rule of <see cref="IRoleScopedSubject"/>, which the permissions
/// apply to the members the agent scopes.
/// </summary>
internal static class AgentChatAccess
{
    /// <summary>Gets why the user of <paramref name="permissions"/> may not chat with <paramref name="agent"/>, or null.</summary>
    public static string? GetRefusal(IUserPermissions permissions, LlmAgentBase agent)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(agent);

        var roles = ((IRoleScopedSubject)agent).ScopeRoles;
        if (permissions.CanActThrough(roles))
        {
            return null;
        }

        if (roles.Count == 0)
        {
            return "You can chat with this agent only if it has roles; ask a full administrator to assign them.";
        }

        var missing = roles.Where(role => !permissions.HoldsAllRoles([role])).ToList();
        if (missing.Count == 0 && permissions.GetRolesWithDenyRulesMissingFrom(roles) is { Count: > 0 } missingWithDenyRules)
        {
            return "You can chat with this agent only if it holds your roles with deny rules, so that it cannot do what they deny you. " +
                $"Missing: {string.Join(", ", missingWithDenyRules)}.";
        }

        // The roles changed between the questions when none is missing; the answer stays a refusal.
        return $"You can chat with this agent only if you hold its roles. Missing: {string.Join(", ", missing.Count == 0 ? roles : missing)}.";
    }
}
