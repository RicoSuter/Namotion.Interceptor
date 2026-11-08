using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authentication.Internal;

/// <summary>
/// Whether role sets reach <see cref="BuiltInRoles.EverythingName"/> through the includes of a given set of custom roles,
/// with no role in reach that denies <c>administer</c>, as <see cref="AuthorizationServiceRoleExtensions.IsFullAdministrator(AuthorizationService, IReadOnlyCollection{string})"/>
/// decides for the current roles.
/// </summary>
internal static class FullAdministrators
{
    /// <summary>
    /// Gets why replacing <paramref name="currentRoles"/> by <paramref name="candidateRoles"/>, each every custom role,
    /// would take <see cref="BuiltInRoles.EverythingName"/> from the people of <paramref name="personRoleSets"/>, or
    /// null. While an unlocked person holds it, a change must leave an unlocked person holding it; while only people an
    /// administrator locked hold it, a change must leave someone holding it. Never refused when nobody
    /// holds it now, so that a change can repair that state. <paramref name="rename"/> is the role the change renames in
    /// every principal, or null.
    /// </summary>
    public static string? GetRemovalError(
        IReadOnlyList<AuthorizationRole> currentRoles,
        IReadOnlyList<AuthorizationRole> candidateRoles,
        IReadOnlyCollection<PersonRoleSet> personRoleSets,
        (string OldName, string NewName)? rename)
    {
        var unlockedRoleSets = personRoleSets.Where(person => !person.IsLockedByAdministrator).Select(person => person.RoleNames).ToList();
        if (AnyHoldsEverything(currentRoles, unlockedRoleSets, rename: null))
        {
            return AnyHoldsEverything(candidateRoles, unlockedRoleSets, rename)
                ? null
                : $"This change would leave no unlocked user with {BuiltInRoles.EverythingName} and no deny rule on {AuthorizationActions.Administer}; give another user {BuiltInRoles.EverythingName} without a role that denies {AuthorizationActions.Administer} first.";
        }

        var allRoleSets = personRoleSets.Select(person => person.RoleNames).ToList();
        if (!AnyHoldsEverything(currentRoles, allRoleSets, rename: null) || AnyHoldsEverything(candidateRoles, allRoleSets, rename))
        {
            return null;
        }

        return $"This change would leave no user with {BuiltInRoles.EverythingName} and no deny rule on {AuthorizationActions.Administer}; give another user {BuiltInRoles.EverythingName} without a role that denies {AuthorizationActions.Administer} first.";
    }

    private static bool AnyHoldsEverything(
        IReadOnlyList<AuthorizationRole> customRoles,
        IReadOnlyCollection<IReadOnlyCollection<string>> roleSets,
        (string OldName, string NewName)? rename)
    {
        var roles = new Dictionary<string, AuthorizationRole>(StringComparer.Ordinal);
        foreach (var role in BuiltInRoles.All.Concat(customRoles))
        {
            roles[role.Name] = role;
        }

        return roleSets.Any(roleSet => HoldsEverything(roleSet, roles, rename));
    }

    private static bool HoldsEverything(
        IReadOnlyCollection<string> roleNames,
        Dictionary<string, AuthorizationRole> roles,
        (string OldName, string NewName)? rename)
    {
        var holdsEverything = false;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roleNames.Select(name =>
            rename is { } renamed && string.Equals(name, renamed.OldName, StringComparison.Ordinal) ? renamed.NewName : name));
        while (pending.TryPop(out var name))
        {
            holdsEverything |= string.Equals(name, BuiltInRoles.EverythingName, StringComparison.Ordinal);
            if (!visited.Add(name) || !roles.TryGetValue(name, out var role))
            {
                continue;
            }

            if (AuthorizationServiceRoleExtensions.DeniesAdminister(role))
            {
                return false;
            }

            foreach (var include in role.Includes)
            {
                pending.Push(include);
            }
        }

        return holdsEverything;
    }
}
