using System.Collections.Frozen;

namespace HomeBlaze.Authorization;

/// <summary>
/// The rule that keeps a person within the deny rules of their roles when they act through another principal: assign
/// roles to a user, act on a user (give them roles, reset or unlock them), or chat with an agent. Deny rules
/// apply across all roles of a principal, so a principal whose role closure lacks a role with deny rules is not bound
/// by them, even when the person holds all of its roles.
/// </summary>
public static class RoleDelegation
{
    /// <summary>An empty set of role names.</summary>
    internal static IReadOnlySet<string> NoRoles { get; } = FrozenSet<string>.Empty;

    /// <summary>
    /// Gets the names of <paramref name="rolesWithDenyRules"/>, the
    /// <see cref="AuthorizationServiceRoleExtensions.GetRolesWithDenyRules"/> of a person, that
    /// <paramref name="targetRoleClosure"/>, the role closure of the principal they act through, lacks, ordered by name.
    /// Empty when there is none, and when <paramref name="targetRoleClosure"/> is empty, as a principal without roles may
    /// do nothing.
    /// </summary>
    public static IReadOnlyList<string> GetMissingRolesWithDenyRules(IReadOnlySet<string> rolesWithDenyRules, IReadOnlySet<string> targetRoleClosure)
    {
        ArgumentNullException.ThrowIfNull(rolesWithDenyRules);
        ArgumentNullException.ThrowIfNull(targetRoleClosure);

        if (rolesWithDenyRules.Count == 0 || targetRoleClosure.Count == 0)
        {
            return [];
        }

        List<string>? missing = null;
        foreach (var role in rolesWithDenyRules)
        {
            if (!targetRoleClosure.Contains(role))
            {
                (missing ??= []).Add(role);
            }
        }

        if (missing is null)
        {
            return [];
        }

        missing.Sort(StringComparer.Ordinal);
        return missing;
    }
}
