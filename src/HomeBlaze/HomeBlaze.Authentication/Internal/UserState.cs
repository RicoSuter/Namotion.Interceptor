using System.Collections.ObjectModel;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;

namespace HomeBlaze.Authentication.Internal;

/// <summary>A user as the user store holds it in memory: what the projection shows, without secrets.</summary>
internal sealed record UserEntry
{
    public required string Id { get; init; }

    public required string UserName { get; init; }

    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    public required ReadOnlyCollection<string> Roles { get; init; }

    public DateTimeOffset? LockoutEnd { get; init; }

    public bool MustChangePassword { get; init; }

    /// <summary>Whether the user signs in with an empty password, as the seeded administrator does until they set one.</summary>
    public bool AllowsEmptyPassword { get; init; }

    public DateTimeOffset? LastLogin { get; init; }

    public bool IsBuiltIn { get; init; }

    public bool IsLockedOut(DateTimeOffset now) => LockoutEnd > now;

    public bool IsLockedByAdministrator => ApplicationUser.IsAdministratorLockoutEnd(LockoutEnd);

    /// <summary>Gets the user as locked by an administrator, or unlocked.</summary>
    public UserEntry WithAdministratorLock(bool isLocked) => this with { LockoutEnd = isLocked ? ApplicationUser.AdministratorLockoutEnd : null };

    /// <summary>Whether <paramref name="user"/> is the built-in user <see cref="NameRules.McpUserName"/>.</summary>
    public static bool IsMcpUser(UserEntry user) =>
        user.IsBuiltIn && string.Equals(user.UserName, NameRules.McpUserName, StringComparison.Ordinal);

    public UserSnapshot ToSnapshot(DateTimeOffset now, bool hasKey) =>
        new(UserName, Email, Roles, IsLockedOut(now), MustChangePassword, LastLogin, IsBuiltIn, hasKey, DisplayName);
}

/// <summary>
/// The users of the user store, indexed by id and by name, and the stored hash of the key of the built-in user
/// <see cref="NameRules.McpUserName"/>; never changed once built.
/// </summary>
internal sealed class UserState
{
    public static readonly UserState Empty = new([], null);

    public UserState(List<UserEntry> users, string? mcpKeyHash)
    {
        McpKeyHash = mcpKeyHash;
        ById = users.ToDictionary(user => user.Id, StringComparer.Ordinal);
        ByName = users.ToDictionary(user => user.UserName, StringComparer.Ordinal);
        RoleSets = [.. users.Select(user => new PrincipalRoleSet(user.UserName, user.Roles))];
    }

    public Dictionary<string, UserEntry> ById { get; }

    public Dictionary<string, UserEntry> ByName { get; }

    public PrincipalRoleSet[] RoleSets { get; }

    /// <summary>Whether the seeded administrator <see cref="AdminSeeding.AdminUserName"/> signs in with an empty password.</summary>
    public bool IsSeededAdministratorUnclaimed =>
        ByName.TryGetValue(AdminSeeding.AdminUserName, out var admin) && admin.AllowsEmptyPassword;

    /// <summary>The stored hash of the key of the built-in user <see cref="NameRules.McpUserName"/>, or null when it has none.</summary>
    public string? McpKeyHash { get; }

    /// <summary>Gets a state with <paramref name="user"/> added, or in place of the user with its id.</summary>
    public UserState With(UserEntry user) =>
        new([.. ById.Values.Where(existing => existing.Id != user.Id), user], McpKeyHash);

    /// <summary>Gets a state without the user with <paramref name="userId"/>.</summary>
    public UserState Without(string userId) =>
        new([.. ById.Values.Where(existing => existing.Id != userId)], McpKeyHash);

    /// <summary>Gets a state in which the users holding the role <paramref name="oldName"/> hold <paramref name="newName"/> instead.</summary>
    public UserState WithRoleRenamed(string oldName, string newName) =>
        new([.. ById.Values.Select(user => user.Roles.Contains(oldName, StringComparer.Ordinal)
            ? user with { Roles = Rename(user.Roles, oldName, newName) }
            : user)], McpKeyHash);

    /// <summary>Gets a state with <paramref name="mcpKeyHash"/> as the stored hash of the key of the built-in user <see cref="NameRules.McpUserName"/>.</summary>
    public UserState WithMcpKeyHash(string? mcpKeyHash) => new([.. ById.Values], mcpKeyHash);

    private static ReadOnlyCollection<string> Rename(IEnumerable<string> roles, string oldName, string newName) =>
        Array.AsReadOnly(roles
            .Select(role => string.Equals(role, oldName, StringComparison.Ordinal) ? newName : role)
            .Order(StringComparer.Ordinal)
            .ToArray());
}

/// <summary>A change of an existing user; <see cref="DisplayName"/> is normalized, see <see cref="NameRules.NormalizeDisplayName"/>.</summary>
internal readonly record struct UserChange(UserAction Action, string[]? Roles = null, string? DisplayName = null)
{
    public static UserChange SetDisplayName(string? displayName) =>
        new(UserAction.SetDisplayName, DisplayName: NameRules.NormalizeDisplayName(displayName));

    public static UserChange SetRoles(IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        return new UserChange(UserAction.SetRoles, Roles: [.. roles]);
    }
}
