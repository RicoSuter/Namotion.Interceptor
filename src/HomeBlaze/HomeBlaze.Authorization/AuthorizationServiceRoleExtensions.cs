using HomeBlaze.Authorization.Internal;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authorization;

/// <summary>
/// Answers questions about the role names of principals and changes of the custom roles with <see cref="AuthorizationService"/>.
/// </summary>
public static class AuthorizationServiceRoleExtensions
{
    /// <summary>
    /// Gets the names of <paramref name="roleNames"/> and of every role they include, directly or through other roles.
    /// A name without a role is kept as given.
    /// </summary>
    public static IReadOnlySet<string> GetRoleClosure(this AuthorizationService service, IReadOnlyCollection<string> roleNames)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(roleNames);

        var snapshot = service.GetSnapshot();
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roleNames);
        while (pending.TryPop(out var name))
        {
            if (string.IsNullOrWhiteSpace(name) || !closure.Add(name))
            {
                continue;
            }

            if (snapshot.TryGetIncludes(name) is { } includes)
            {
                foreach (var include in includes)
                {
                    pending.Push(include);
                }
            }
        }

        return closure;
    }

    /// <summary>
    /// Gets the names among <paramref name="roleClosure"/>, a <see cref="GetRoleClosure"/>, of the roles that have a deny
    /// rule of their own, and of the names without a usable role, whose rules are unknown. The
    /// <see cref="SystemRules"/> are not roles and never count. See <see cref="RoleDelegation"/> for what they bind.
    /// </summary>
    public static IReadOnlySet<string> GetRolesWithDenyRules(this AuthorizationService service, IReadOnlySet<string> roleClosure)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(roleClosure);

        var snapshot = service.GetSnapshot();
        HashSet<string>? rolesWithDenyRules = null;
        foreach (var name in roleClosure)
        {
            if (snapshot.HasDenyRules(name) is not false)
            {
                (rolesWithDenyRules ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
            }
        }

        return rolesWithDenyRules ?? RoleDelegation.NoRoles;
    }

    /// <summary>
    /// Whether a person holding <paramref name="roleNames"/> is a full administrator: the closure of the names contains
    /// <see cref="BuiltInRoles.EverythingName"/> and the names compile into the policy of a person, so that a set naming an
    /// unknown or broken role, whose policy denies everything, never counts.
    /// </summary>
    public static bool IsFullAdministrator(this AuthorizationService service, IReadOnlyCollection<string> roleNames) =>
        service.IsFullAdministrator(roleNames, service.GetRoleClosure(roleNames));

    /// <summary>
    /// As <see cref="IsFullAdministrator(AuthorizationService, IReadOnlyCollection{string})"/>, with
    /// <paramref name="roleClosure"/>, the <see cref="GetRoleClosure"/> of <paramref name="roleNames"/>, already known.
    /// </summary>
    public static bool IsFullAdministrator(this AuthorizationService service, IReadOnlyCollection<string> roleNames, IReadOnlySet<string> roleClosure)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(roleClosure);

        return roleClosure.Contains(BuiltInRoles.EverythingName) && service.GetPolicy(roleNames, PrincipalKind.Person).Error is null;
    }

    /// <summary>
    /// Gets why replacing the custom roles <paramref name="currentRoles"/> by <paramref name="candidateRoles"/>, each the
    /// complete set of custom roles, would break a principal, or null. A principal breaks when its roles compile now,
    /// with the system rules of a person, and would not compile, or would name a role that does not exist, with the
    /// candidate; one whose roles fail now never blocks a change, so a change can repair it.
    /// </summary>
    /// <returns>The principals that would break and the reason for the first of them, or null.</returns>
    public static string? ValidateRoles(
        this AuthorizationService service,
        IReadOnlyList<AuthorizationRole> currentRoles,
        IReadOnlyList<AuthorizationRole> candidateRoles,
        IEnumerable<PrincipalRoleSet> principalRoleSets)
    {
        ArgumentNullException.ThrowIfNull(service);

        return RoleChangeValidation.Validate(currentRoles, candidateRoles, principalRoleSets, rename: null, service.PathProvider);
    }

    /// <summary>
    /// As <see cref="ValidateRoles(AuthorizationService, IReadOnlyList{AuthorizationRole}, IReadOnlyList{AuthorizationRole}, IEnumerable{PrincipalRoleSet})"/>,
    /// for a change that renames the role <paramref name="oldName"/> to <paramref name="newName"/> in every principal,
    /// so that the role sets are compiled with the new name against the candidate.
    /// </summary>
    public static string? ValidateRoles(
        this AuthorizationService service,
        IReadOnlyList<AuthorizationRole> currentRoles,
        IReadOnlyList<AuthorizationRole> candidateRoles,
        IEnumerable<PrincipalRoleSet> principalRoleSets,
        string oldName,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(oldName);
        ArgumentNullException.ThrowIfNull(newName);

        return RoleChangeValidation.Validate(currentRoles, candidateRoles, principalRoleSets, (oldName, newName), service.PathProvider);
    }
}
