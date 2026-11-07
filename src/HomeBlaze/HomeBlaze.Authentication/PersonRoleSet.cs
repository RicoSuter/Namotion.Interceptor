namespace HomeBlaze.Authentication;

/// <summary>
/// The role names of a user who is a person, not a built-in user, and whether an administrator locked them; a lockout
/// after failed sign-ins does not count, as it ends by itself.
/// </summary>
public readonly record struct PersonRoleSet(IReadOnlyCollection<string> RoleNames, bool IsLockedByAdministrator);
