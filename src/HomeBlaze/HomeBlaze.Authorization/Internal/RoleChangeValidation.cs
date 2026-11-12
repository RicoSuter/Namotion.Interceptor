using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Authorization.Internal;

/// <summary>Decides whether a change of the custom roles breaks a principal, see <see cref="AuthorizationServiceRoleExtensions.ValidateRoles(AuthorizationService, IReadOnlyList{AuthorizationRole}, IReadOnlyList{AuthorizationRole}, IEnumerable{PrincipalRoleSet})"/>.</summary>
internal static class RoleChangeValidation
{
    public static string? Validate(
        IReadOnlyList<AuthorizationRole> currentRoles,
        IReadOnlyList<AuthorizationRole> candidateRoles,
        IEnumerable<PrincipalRoleSet> principalRoleSets,
        (string Old, string New)? rename,
        PathProviderBase pathProvider)
    {
        ArgumentNullException.ThrowIfNull(currentRoles);
        ArgumentNullException.ThrowIfNull(candidateRoles);
        ArgumentNullException.ThrowIfNull(principalRoleSets);

        var current = AuthorizationSnapshot.BuildCandidate(currentRoles, pathProvider);
        var candidate = AuthorizationSnapshot.BuildCandidate(candidateRoles, pathProvider);
        List<string>? brokenPrincipals = null;
        string? firstError = null;
        foreach (var (principal, roleNames) in principalRoleSets)
        {
            // A principal whose roles fail now cannot be broken further, and must not block the change that repairs it.
            if (roleNames.Count == 0 || current.GetPolicy(roleNames, PrincipalKind.Person).Error is not null)
            {
                continue;
            }

            var candidateNames = rename is { } names
                ? roleNames.Select(name => string.Equals(name, names.Old, StringComparison.Ordinal) ? names.New : name).ToList()
                : roleNames;
            if (candidate.GetPolicy(candidateNames, PrincipalKind.Person).Error is { } error)
            {
                firstError ??= error;
                brokenPrincipals ??= [];
                if (!brokenPrincipals.Contains(principal, StringComparer.Ordinal))
                {
                    brokenPrincipals.Add(principal);
                }
            }
        }

        return brokenPrincipals is null
            ? null
            : $"This change would leave {string.Join(", ", brokenPrincipals)} without permissions: {firstError}";
    }
}
