using HomeBlaze.Authorization;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using AuthorizationSubject = HomeBlaze.Authorization.Authorization;

namespace HomeBlaze.Authentication.Internal;

/// <summary>
/// Decides whether an acting person may make a change of users: they must be a person with an active account whose
/// policy allows <c>administer</c> on the members the change goes through (the operation, and the roles of the user it
/// changes), the change must pass the delegation rules,
/// never leave the installation without an unlocked full administrator, and assign only known roles that compile.
/// Every check denies when its input is missing. Called under the <see cref="AuthorizationChangeGate"/>.
/// </summary>
internal sealed class UserChangeRules(
    AuthorizationService authorizationService,
    RoleStore roleStore,
    AuthorizationProjection projection,
    TimeProvider timeProvider)
{
    /// <summary>Gets why the acting person may not add a user named <paramref name="userName"/> holding <paramref name="roles"/>, or null.</summary>
    public string? GetAddRefusal(UserState state, string actingUserId, string userName, string[] roles)
    {
        if (GetActor(state, actingUserId) is not { } actor)
        {
            return UserStore.ActorRefusal;
        }

        if (!MayAdminister(actor.Roles, GetMember(projection.Target, nameof(AuthorizationSubject.AddUserAsync))))
        {
            return "You are not allowed to add users.";
        }

        var newUser = new UserTarget(string.Empty, false, [], new HashSet<string>());
        return GetUserNameError(state, userName)
            ?? UserDelegation.GetRefusal(actor.Delegation, newUser, UserAction.SetRoles, roles, authorizationService.GetRoleClosure(roles))
            ?? GetRolesError(userName, roles);
    }

    /// <summary>
    /// Gets why the acting person may not make <paramref name="change"/> to <paramref name="target"/>, the user named
    /// <paramref name="userName"/> or null when there is none, or null.
    /// </summary>
    public string? GetChangeRefusal(UserState state, string actingUserId, string userName, UserEntry? target, UserChange change)
    {
        if (GetActor(state, actingUserId) is not { } actor)
        {
            return UserStore.ActorRefusal;
        }

        if (GetAccessRefusal(actor, userName, target, change) is { } accessRefusal)
        {
            return accessRefusal;
        }

        if (target!.IsBuiltIn && change.Action != UserAction.SetRoles)
        {
            return GetBuiltInRefusal(target.UserName, change.Action);
        }

        var userTarget = new UserTarget(target.Id, target.IsBuiltIn, target.Roles, authorizationService.GetRoleClosure(target.Roles));
        var newRoleClosure = change.Roles is { } newRoles ? authorizationService.GetRoleClosure(newRoles) : null;
        return UserDelegation.GetRefusal(actor.Delegation, userTarget, change.Action, change.Roles, newRoleClosure)
            ?? (RemovesFullAdministration(change) ? GetLastAdministratorRefusal(state, target) : null)
            ?? (change.Roles is { } roles ? GetRolesError(target.UserName, roles) : null);
    }

    /// <summary>
    /// Gets why the acting person may not create or revoke the key of <paramref name="mcpUser"/>, the built-in user
    /// <see cref="NameRules.McpUserName"/> or null when there is none, or null. Only a full administrator may.
    /// </summary>
    public string? GetMcpKeyRefusal(UserState state, string actingUserId, UserEntry? mcpUser, bool create)
    {
        if (GetActor(state, actingUserId) is not { } actor)
        {
            return UserStore.ActorRefusal;
        }

        if (!authorizationService.IsFullAdministrator(actor.Roles))
        {
            return $"Only a full administrator can manage the key of the built-in user '{NameRules.McpUserName}'.";
        }

        if (mcpUser is null)
        {
            return $"The user '{NameRules.McpUserName}' does not exist.";
        }

        var user = projection.Target?.Users.GetValueOrDefault(mcpUser.UserName);
        var methodName = create ? nameof(User.CreateKeyAsync) : nameof(User.RevokeKeyAsync);
        return MayAdminister(actor.Roles, GetMember(user, methodName))
            ? null
            : GetNotAllowedRefusal(mcpUser.UserName);
    }

    /// <summary>
    /// Gets why the person with <paramref name="userId"/> may not change their own account, or null: they must be a person
    /// with an active account. Changing one's own account needs no <c>administer</c>.
    /// </summary>
    public string? GetOwnChangeRefusal(UserState state, string userId) =>
        GetActor(state, userId) is null ? UserStore.OwnAccountRefusal : null;

    // Whether a user exists is told only to a person who may manage the users.
    private string? GetAccessRefusal(ActingUser actor, string userName, UserEntry? target, UserChange change)
    {
        if (target is null)
        {
            return MayAdminister(actor.Roles, projection.Target?.TryGetRegisteredSubject()?.TryGetProperty(nameof(AuthorizationSubject.Users)))
                ? $"The user '{userName}' does not exist."
                : GetNotAllowedRefusal(userName);
        }

        var user = projection.Target?.Users.GetValueOrDefault(target.UserName);
        return MayAdminister(actor.Roles, user?.TryGetRegisteredSubject()?.TryGetProperty(nameof(User.Roles))) &&
            MayAdminister(actor.Roles, GetMember(user, GetOperationMethodName(change)))
                ? null
                : GetNotAllowedRefusal(target.UserName);
    }

    // An actor is a person: a user who exists, is not built in and is not locked out.
    private ActingUser? GetActor(UserState state, string actingUserId)
    {
        if (!state.ById.TryGetValue(actingUserId, out var user) || user.IsBuiltIn || user.IsLockedOut(timeProvider.GetUtcNow()))
        {
            return null;
        }

        var roleClosure = authorizationService.GetRoleClosure(user.Roles);
        return new ActingUser(user.Roles, new UserActor(
            user.Id, roleClosure, authorizationService.IsFullAdministrator(user.Roles, roleClosure), authorizationService.GetRolesWithDenyRules(roleClosure)));
    }

    private bool MayAdminister(IReadOnlyCollection<string> actorRoles, RegisteredSubjectProperty? member)
    {
        if (member is null)
        {
            return false;
        }

        var result = authorizationService.GetPolicy(actorRoles, PrincipalKind.Person);
        return result.Error is null && result.Policy.IsAllowed(member, AuthorizationActions.Administer);
    }

    private static string GetBuiltInRefusal(string userName, UserAction action) =>
        action == UserAction.SetDisplayName
            ? $"The built-in user '{userName}' has no display name."
            : $"The built-in user '{userName}' cannot be removed, locked or have its password reset.";

    private static string GetNotAllowedRefusal(string userName) => $"You are not allowed to manage the user '{userName}'.";

    // A method property is named after its method without the Async suffix.
    private static RegisteredSubjectProperty? GetMember(IInterceptorSubject? subject, string methodName) =>
        subject?.TryGetRegisteredSubject()?.TryGetProperty(methodName[..^"Async".Length]);

    private static string GetOperationMethodName(UserChange change) => change.Action switch
    {
        UserAction.Remove => nameof(User.RemoveAsync),
        UserAction.Reset => nameof(User.ResetPasswordAsync),
        UserAction.Lock => nameof(User.LockAsync),
        UserAction.Unlock => nameof(User.UnlockAsync),
        UserAction.SetDisplayName => nameof(User.SetDisplayNameAsync),
        _ => nameof(User.SetRolesAsync)
    };

    private bool RemovesFullAdministration(UserChange change) => change.Action switch
    {
        UserAction.Remove or UserAction.Lock => true,
        UserAction.SetRoles => !authorizationService.IsFullAdministrator(change.Roles ?? []),
        _ => false
    };

    private string? GetLastAdministratorRefusal(UserState state, UserEntry target)
    {
        var now = timeProvider.GetUtcNow();
        if (!IsActiveFullAdministrator(target, now) ||
            state.ById.Values.Any(user => user.Id != target.Id && IsActiveFullAdministrator(user, now)))
        {
            return null;
        }

        return $"'{target.UserName}' is the last unlocked full administrator; give another user {BuiltInRoles.EverythingName} first.";
    }

    private bool IsActiveFullAdministrator(UserEntry user, DateTimeOffset now) =>
        !user.IsBuiltIn && !user.IsLockedOut(now) && authorizationService.IsFullAdministrator(user.Roles);

    private static string? GetUserNameError(UserState state, string userName)
    {
        if (NameRules.GetNameError(userName, "user name") is { } error)
        {
            return error;
        }

        if (NameRules.IsBuiltInUserName(userName))
        {
            return $"The user name '{userName}' is reserved for a built-in user.";
        }

        return state.ByName.ContainsKey(userName) ? $"A user named '{userName}' already exists." : null;
    }

    private string? GetRolesError(string userName, string[] roles)
    {
        foreach (var role in roles)
        {
            if (roleStore.GetRoleName(role) is null)
            {
                return $"Unknown role: {role}";
            }
        }

        // A role set that does not compile would leave the user with nothing allowed.
        return roles.Length > 0 && authorizationService.GetPolicy(roles, GetKind(userName)).Error is { } error ? error : null;
    }

    private static PrincipalKind GetKind(string userName)
    {
        if (string.Equals(userName, NameRules.AnonymousUserName, StringComparison.Ordinal))
        {
            return PrincipalKind.Anonymous;
        }

        return string.Equals(userName, NameRules.McpUserName, StringComparison.Ordinal) ? PrincipalKind.NonHuman : PrincipalKind.Person;
    }

    private sealed record ActingUser(IReadOnlyCollection<string> Roles, UserActor Delegation);
}
