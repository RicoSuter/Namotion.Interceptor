using HomeBlaze.Authorization;

namespace HomeBlaze.Authentication;

/// <summary>
/// The person who changes a user, with the closure of their roles, whether they are a full administrator, as
/// <see cref="AuthorizationServiceRoleExtensions.IsFullAdministrator(AuthorizationService, IReadOnlyCollection{string}, IReadOnlySet{string})"/>
/// decides, and the roles of the closure with deny rules, as
/// <see cref="AuthorizationServiceRoleExtensions.GetRolesWithDenyRules"/> gives them.
/// </summary>
internal sealed record UserActor(string UserId, IReadOnlySet<string> RoleClosure, bool IsFullAdministrator, IReadOnlySet<string> RolesWithDenyRules);

/// <summary>The user being changed.</summary>
internal sealed record UserTarget(string UserId, bool IsBuiltIn, IReadOnlyCollection<string> Roles, IReadOnlySet<string> RoleClosure);

/// <summary>A change of a user that the delegation rules check.</summary>
internal enum UserAction
{
    /// <summary>Changing the roles of the user.</summary>
    SetRoles,

    /// <summary>Removing the user.</summary>
    Remove,

    /// <summary>Locking the user.</summary>
    Lock,

    /// <summary>Resetting the password of the user.</summary>
    Reset,

    /// <summary>Unlocking the user.</summary>
    Unlock,

    /// <summary>Setting the display name of the user.</summary>
    SetDisplayName
}

/// <summary>
/// The rules for a person who manages users: nobody resets their own password. A person who is not a full administrator
/// also assigns and removes only roles they hold, never changes their own account, never acts on a user holding a role
/// they do not hold, and never touches built-in users.
/// Every person, full administrators included, follows <see cref="RoleDelegation"/>: the user they act on and the roles
/// they assign hold each of their roles with deny rules, except for a change that only takes access away: locking,
/// removing, or changing the roles to none or to a subset of the role closure the user holds that keeps the roles with
/// deny rules of the person that the user holds.
/// </summary>
internal static class UserDelegation
{
    /// <summary>The refusal of resetting one's own password, which everybody gets.</summary>
    public const string OwnPasswordResetRefusal = "You cannot reset your own password. Change it in the user menu under Account > Change your password.";

    /// <summary>Gets why <paramref name="actor"/> may not do <paramref name="action"/> on <paramref name="target"/>, or null.</summary>
    /// <param name="actor">The person who changes the user.</param>
    /// <param name="target">The user, with the roles they hold before the change; a user to add holds none.</param>
    /// <param name="action">The change.</param>
    /// <param name="newRoles">The roles the change assigns, or null when it assigns none.</param>
    /// <param name="newRoleClosure">The closure of <paramref name="newRoles"/>; given with them.</param>
    public static string? GetRefusal(
        UserActor actor, UserTarget target, UserAction action, IReadOnlyCollection<string>? newRoles, IReadOnlySet<string>? newRoleClosure)
    {
        if (newRoles is not null)
        {
            ArgumentNullException.ThrowIfNull(newRoleClosure);
        }

        var isOwnAccount = string.Equals(actor.UserId, target.UserId, StringComparison.Ordinal);
        // The reset would end the session of the person before its setup code is shown, leaving them with neither.
        if (isOwnAccount && action == UserAction.Reset)
        {
            return OwnPasswordResetRefusal;
        }

        return (actor.IsFullAdministrator ? null : GetManagerRefusal(actor, target, action, newRoles, isOwnAccount))
            ?? GetDenyRulesRefusal(actor, target, action, newRoleClosure);
    }

    private static string? GetManagerRefusal(UserActor actor, UserTarget target, UserAction action, IReadOnlyCollection<string>? newRoles, bool isOwnAccount)
    {
        if (target.IsBuiltIn)
        {
            return "Only a full administrator can change the built-in users.";
        }

        if (isOwnAccount)
        {
            return action == UserAction.SetRoles ? "You cannot change your own roles." : "You cannot do this to your own account.";
        }

        var notHeld = target.RoleClosure.Where(role => !actor.RoleClosure.Contains(role)).ToList();
        if (notHeld.Count > 0)
        {
            return $"This user holds roles you do not hold: {string.Join(", ", notHeld)}.";
        }

        if (action == UserAction.SetRoles && newRoles is not null)
        {
            var notAssignable = newRoles.Where(role => !actor.RoleClosure.Contains(role)).ToList();
            if (notAssignable.Count > 0)
            {
                return $"You can assign only roles you hold. Not held: {string.Join(", ", notAssignable)}.";
            }
        }

        return null;
    }

    private static string? GetDenyRulesRefusal(UserActor actor, UserTarget target, UserAction action, IReadOnlySet<string>? newRoleClosure)
    {
        // A user can do less after such a change than before, so the person cannot escape their deny rules through it.
        if (OnlyTakesAccessAway(actor, target, action, newRoleClosure))
        {
            return null;
        }

        var notHeldByTarget = RoleDelegation.GetMissingRolesWithDenyRules(actor.RolesWithDenyRules, target.RoleClosure);
        if (notHeldByTarget.Count > 0)
        {
            return $"This user does not hold your roles with deny rules: {string.Join(", ", notHeldByTarget)}.";
        }

        var notAssigned = newRoleClosure is null ? [] : RoleDelegation.GetMissingRolesWithDenyRules(actor.RolesWithDenyRules, newRoleClosure);
        return notAssigned.Count > 0
            ? $"The assigned roles must include your roles with deny rules: {string.Join(", ", notAssigned)}."
            : null;
    }

    private static bool OnlyTakesAccessAway(UserActor actor, UserTarget target, UserAction action, IReadOnlySet<string>? newRoleClosure)
    {
        switch (action)
        {
            case UserAction.Lock or UserAction.Remove:
                return true;

            case UserAction.SetRoles when newRoleClosure is not null:
                if (newRoleClosure.Count == 0)
                {
                    return true;
                }

                foreach (var role in newRoleClosure)
                {
                    if (!target.RoleClosure.Contains(role))
                    {
                        return false;
                    }
                }

                // Dropping a role with deny rules lifts its deny rules from the user, which grants access.
                foreach (var role in actor.RolesWithDenyRules)
                {
                    if (target.RoleClosure.Contains(role) && !newRoleClosure.Contains(role))
                    {
                        return false;
                    }
                }

                return true;

            default:
                return false;
        }
    }
}
