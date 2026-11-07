namespace HomeBlaze.Authorization;

/// <summary>A user of the authentication database as <see cref="Authorization.Users"/> shows it.</summary>
/// <param name="UserName">The name the user signs in with, their key in <see cref="Authorization.Users"/>.</param>
/// <param name="Email">The email address, or null.</param>
/// <param name="Roles">The names of the roles assigned to the user.</param>
/// <param name="IsLockedOut">Whether the user cannot sign in because they are locked out.</param>
/// <param name="MustChangePassword">Whether the user must set a password before anything else.</param>
/// <param name="LastLogin">The time of the last successful sign-in, or null.</param>
/// <param name="IsBuiltIn">Whether this is a built-in user, such as <see cref="NameRules.AnonymousUserName"/>.</param>
/// <param name="HasKey">Whether this is the built-in user <see cref="NameRules.McpUserName"/> and it has a key.</param>
/// <param name="DisplayName">The name shown for the user in place of <paramref name="UserName"/>, or null.</param>
public sealed record UserSnapshot(
    string UserName,
    string? Email,
    IReadOnlyList<string> Roles,
    bool IsLockedOut,
    bool MustChangePassword,
    DateTimeOffset? LastLogin,
    bool IsBuiltIn,
    bool HasKey,
    string? DisplayName = null);
