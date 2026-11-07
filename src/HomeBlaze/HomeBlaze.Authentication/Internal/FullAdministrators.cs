using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authentication.Internal;

/// <summary>Whether role sets reach <see cref="BuiltInRoles.EverythingName"/> through the includes of a given set of custom roles.</summary>
internal static class FullAdministrators
{
    /// <summary>
    /// Gets why replacing <paramref name="currentRoles"/> by <paramref name="candidateRoles"/>, each every custom role,
    /// would take <see cref="BuiltInRoles.EverythingName"/> from the people of <paramref name="personRoleSets"/>, or
    /// null. While an unlocked person holds it, a change must leave an unlocked person holding it; while only locked-out
    /// people hold it, as during a temporary lockout, a change must leave someone holding it. Never refused when nobody
    /// holds it now, so that a change can repair that state. <paramref name="rename"/> is the role the change renames in
    /// every principal, or null.
    /// </summary>
    public static string? GetRemovalError(
        IReadOnlyList<AuthorizationRole> currentRoles,
        IReadOnlyList<AuthorizationRole> candidateRoles,
        IReadOnlyCollection<PersonRoleSet> personRoleSets,
        (string OldName, string NewName)? rename)
    {
        var unlockedRoleSets = personRoleSets.Where(person => !person.IsLockedOut).Select(person => person.RoleNames).ToList();
        if (AnyHoldsEverything(currentRoles, unlockedRoleSets, rename: null))
        {
            return AnyHoldsEverything(candidateRoles, unlockedRoleSets, rename)
                ? null
                : $"This change would leave no unlocked user with {BuiltInRoles.EverythingName}; give another user {BuiltInRoles.EverythingName} first.";
        }

        var allRoleSets = personRoleSets.Select(person => person.RoleNames).ToList();
        if (!AnyHoldsEverything(currentRoles, allRoleSets, rename: null) || AnyHoldsEverything(candidateRoles, allRoleSets, rename))
        {
            return null;
        }

        return $"This change would leave no user with {BuiltInRoles.EverythingName}; give another user {BuiltInRoles.EverythingName} first.";
    }

    private static bool AnyHoldsEverything(
        IReadOnlyList<AuthorizationRole> customRoles,
        IReadOnlyCollection<IReadOnlyCollection<string>> roleSets,
        (string OldName, string NewName)? rename)
    {
        var includes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var role in BuiltInRoles.All.Concat(customRoles))
        {
            includes[role.Name] = role.Includes;
        }

        return roleSets.Any(roleSet => HoldsEverything(roleSet, includes, rename));
    }

    private static bool HoldsEverything(
        IReadOnlyCollection<string> roleNames,
        Dictionary<string, IReadOnlyList<string>> includes,
        (string OldName, string NewName)? rename)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roleNames.Select(name =>
            rename is { } renamed && string.Equals(name, renamed.OldName, StringComparison.Ordinal) ? renamed.NewName : name));
        while (pending.TryPop(out var name))
        {
            if (string.Equals(name, BuiltInRoles.EverythingName, StringComparison.Ordinal))
            {
                return true;
            }

            if (visited.Add(name) && includes.TryGetValue(name, out var included))
            {
                foreach (var include in included)
                {
                    pending.Push(include);
                }
            }
        }

        return false;
    }
}
