namespace HomeBlaze.Authentication;

/// <summary>The role names of a user who is a person, not a built-in user, and whether they are locked out now.</summary>
public readonly record struct PersonRoleSet(IReadOnlyCollection<string> RoleNames, bool IsLockedOut);
