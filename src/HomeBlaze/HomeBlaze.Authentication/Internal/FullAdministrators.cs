using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authentication.Internal;

/// <summary>
/// The rule that keeps a full administrator, a person whose roles reach <see cref="BuiltInRoles.EverythingName"/> through
/// the includes of the custom roles, with no role in reach that denies <c>administer</c>, and whose roles are all known and
/// compile, as <see cref="AuthorizationServiceRoleExtensions.IsFullAdministrator(AuthorizationService, IReadOnlyCollection{string})"/>
/// decides, for changes of users and of roles alike.
/// </summary>
internal static class FullAdministrators
{
    /// <summary>
    /// Gets why replacing the custom roles <paramref name="currentRoles"/> by <paramref name="candidateRoles"/>, each every
    /// custom role, and the people <paramref name="currentPeople"/> by <paramref name="candidatePeople"/>, would take the
    /// last full administrator away, or null. While an unlocked full administrator exists, a change must leave one; while
    /// only full administrators an administrator locked exist, a change must leave one of them. Never refused while none
    /// exists, so that a change can repair that state. A change of users passes the same roles twice, and a change of
    /// roles the same people, with a renamed role renamed in <paramref name="candidatePeople"/>.
    /// </summary>
    public static string? GetRemovalError(
        AuthorizationService service,
        IReadOnlyList<AuthorizationRole> currentRoles,
        IReadOnlyList<AuthorizationRole> candidateRoles,
        IReadOnlyCollection<PersonRoleSet> currentPeople,
        IReadOnlyCollection<PersonRoleSet> candidatePeople)
    {
        var current = service.GetFullAdministratorTest(currentRoles);
        var candidate = ReferenceEquals(candidateRoles, currentRoles) ? current : service.GetFullAdministratorTest(candidateRoles);
        if (Holds(currentPeople, current, includeLocked: false))
        {
            return Holds(candidatePeople, candidate, includeLocked: false)
                ? null
                : $"This change would leave no unlocked user with {BuiltInRoles.EverythingName} and no deny rule on {AuthorizationActions.Administer}; give another user {BuiltInRoles.EverythingName} without a role that denies {AuthorizationActions.Administer} first.";
        }

        if (!Holds(currentPeople, current, includeLocked: true) || Holds(candidatePeople, candidate, includeLocked: true))
        {
            return null;
        }

        return $"This change would leave no user with {BuiltInRoles.EverythingName} and no deny rule on {AuthorizationActions.Administer}; give another user {BuiltInRoles.EverythingName} without a role that denies {AuthorizationActions.Administer} first.";
    }

    private static bool Holds(IReadOnlyCollection<PersonRoleSet> people, Func<IReadOnlyCollection<string>, bool> isFullAdministrator, bool includeLocked) =>
        people.Any(person => (includeLocked || !person.IsLockedByAdministrator) && isFullAdministrator(person.RoleNames));
}
