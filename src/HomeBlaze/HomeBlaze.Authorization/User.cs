using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Authorization;

/// <summary>
/// A user of the installation, shown from the authentication database. Its members change only through the user
/// store; its name is its key in the users of the authorization.
/// </summary>
[Description("A person who signs in, or a built-in principal")]
[DisplayName("User")]
[SubjectIcon("Person")]
[ExcludeFromCreation]
[InterceptorSubject]
public partial class User : ITitleProvider
{
    /// <summary>The name the user signs in with.</summary>
    [State]
    public partial string UserName { get; internal set; }

    /// <summary>The name shown for the user in place of <see cref="UserName"/>, or null.</summary>
    [State]
    public partial string? DisplayName { get; internal set; }

    /// <summary>The email address, or null.</summary>
    [State]
    public partial string? Email { get; internal set; }

    /// <summary>The names of the roles assigned to the user.</summary>
    [State]
    public partial IReadOnlyList<string> Roles { get; internal set; }

    /// <summary>Whether the user cannot sign in because they are locked out.</summary>
    [State]
    public partial bool IsLockedOut { get; internal set; }

    /// <summary>Whether the user must set a password before anything else.</summary>
    [State]
    public partial bool MustChangePassword { get; internal set; }

    /// <summary>The time of the last successful sign-in, or null.</summary>
    [State]
    public partial DateTimeOffset? LastLogin { get; internal set; }

    /// <summary>Whether this is a built-in user, which cannot sign in, be removed, locked or reset.</summary>
    [State]
    public partial bool IsBuiltIn { get; internal set; }

    /// <summary>
    /// Whether clients of the external MCP endpoint must present a key; only the built-in user
    /// <see cref="NameRules.McpUserName"/> has one, so it is false for every other user.
    /// </summary>
    [State]
    public partial bool HasKey { get; internal set; }

    /// <summary>
    /// What makes a built-in user a risk, or null: <see cref="NameRules.AnonymousUserName"/> holding roles, which apply
    /// to everyone who can reach the user interface, or <see cref="NameRules.McpUserName"/> holding more than
    /// <see cref="BuiltInRoles.ReaderName"/> without a key.
    /// </summary>
    [State]
    public partial string? Warning { get; internal set; }

    /// <summary>
    /// The display name followed by the user name in parentheses, so that users with the same display name can be told
    /// apart, or the user name when there is no display name.
    /// </summary>
    public string Title => DisplayName is { } displayName ? $"{displayName} ({UserName})" : UserName;

    private const string BuiltInUserReason = "Built-in users cannot be removed, reset, locked or unlocked.";
    private const string BuiltInDisplayNameReason = "Built-in users have no display name.";
    private static readonly string KeyReason = $"Only the built-in user {NameRules.McpUserName} has a key.";

    /// <summary>Whether <see cref="RemoveAsync"/> applies: not for a built-in user, which the user store refuses.</summary>
    [Derived]
    [PropertyAttribute("Remove", KnownAttributes.IsEnabled)]
    public bool IsRemoveEnabled => !IsBuiltIn;

    /// <summary>Why <see cref="RemoveAsync"/> does not apply, or null.</summary>
    [Derived]
    [PropertyAttribute("Remove", KnownAttributes.DisabledReason)]
    public string? RemoveDisabledReason => IsBuiltIn ? BuiltInUserReason : null;

    /// <summary>Whether <see cref="ResetPasswordAsync"/> applies: not for a built-in user, which has no password.</summary>
    [Derived]
    [PropertyAttribute("ResetPassword", KnownAttributes.IsEnabled)]
    public bool IsResetPasswordEnabled => !IsBuiltIn;

    /// <summary>Why <see cref="ResetPasswordAsync"/> does not apply, or null.</summary>
    [Derived]
    [PropertyAttribute("ResetPassword", KnownAttributes.DisabledReason)]
    public string? ResetPasswordDisabledReason => IsBuiltIn ? BuiltInUserReason : null;

    /// <summary>Whether <see cref="LockAsync"/> applies: not for a built-in user, which cannot sign in.</summary>
    [Derived]
    [PropertyAttribute("Lock", KnownAttributes.IsEnabled)]
    public bool IsLockEnabled => !IsBuiltIn;

    /// <summary>Why <see cref="LockAsync"/> does not apply, or null.</summary>
    [Derived]
    [PropertyAttribute("Lock", KnownAttributes.DisabledReason)]
    public string? LockDisabledReason => IsBuiltIn ? BuiltInUserReason : null;

    /// <summary>Whether <see cref="UnlockAsync"/> applies: not for a built-in user, which cannot sign in.</summary>
    [Derived]
    [PropertyAttribute("Unlock", KnownAttributes.IsEnabled)]
    public bool IsUnlockEnabled => !IsBuiltIn;

    /// <summary>Why <see cref="UnlockAsync"/> does not apply, or null.</summary>
    [Derived]
    [PropertyAttribute("Unlock", KnownAttributes.DisabledReason)]
    public string? UnlockDisabledReason => IsBuiltIn ? BuiltInUserReason : null;

    /// <summary>Whether <see cref="SetDisplayNameAsync"/> applies: not for a built-in user, which has no display name.</summary>
    [Derived]
    [PropertyAttribute("SetDisplayName", KnownAttributes.IsEnabled)]
    public bool IsSetDisplayNameEnabled => !IsBuiltIn;

    /// <summary>Why <see cref="SetDisplayNameAsync"/> does not apply, or null.</summary>
    [Derived]
    [PropertyAttribute("SetDisplayName", KnownAttributes.DisabledReason)]
    public string? SetDisplayNameDisabledReason => IsBuiltIn ? BuiltInDisplayNameReason : null;

    /// <summary>Whether <see cref="CreateKeyAsync"/> applies: only for the built-in user <see cref="NameRules.McpUserName"/>.</summary>
    [Derived]
    [PropertyAttribute("CreateKey", KnownAttributes.IsEnabled)]
    public bool IsCreateKeyEnabled => IsMcpUser;

    /// <summary>Why <see cref="CreateKeyAsync"/> does not apply, or null.</summary>
    [Derived]
    [PropertyAttribute("CreateKey", KnownAttributes.DisabledReason)]
    public string? CreateKeyDisabledReason => IsMcpUser ? null : KeyReason;

    /// <summary>Whether <see cref="RevokeKeyAsync"/> applies: only for the built-in user <see cref="NameRules.McpUserName"/>.</summary>
    [Derived]
    [PropertyAttribute("RevokeKey", KnownAttributes.IsEnabled)]
    public bool IsRevokeKeyEnabled => IsMcpUser;

    /// <summary>Why <see cref="RevokeKeyAsync"/> does not apply, or null.</summary>
    [Derived]
    [PropertyAttribute("RevokeKey", KnownAttributes.DisabledReason)]
    public string? RevokeKeyDisabledReason => IsMcpUser ? null : KeyReason;

    private bool IsMcpUser => IsBuiltIn && string.Equals(UserName, NameRules.McpUserName, StringComparison.Ordinal);

    public User()
    {
        UserName = string.Empty;
        DisplayName = null;
        Email = null;
        Roles = [];
        IsLockedOut = false;
        MustChangePassword = false;
        LastLogin = null;
        IsBuiltIn = false;
        HasKey = false;
        Warning = null;
    }

    internal static string? GetWarning(UserSnapshot user)
    {
        if (!user.IsBuiltIn)
        {
            return null;
        }

        if (IsNamed(user, NameRules.AnonymousUserName) && user.Roles.Count > 0)
        {
            return $"{NameRules.AnonymousUserName} holds {DescribeRoles(user.Roles)}: everyone who can reach the user interface gets these roles without signing in. Give {NameRules.AnonymousUserName} roles only on a trusted network.";
        }

        return IsNamed(user, NameRules.McpUserName) && !user.HasKey &&
            user.Roles.Any(role => !string.Equals(role, BuiltInRoles.ReaderName, StringComparison.Ordinal))
                ? $"{NameRules.McpUserName} holds more than {BuiltInRoles.ReaderName} ({DescribeRoles(user.Roles)}) and has no key: every client that reaches the external MCP endpoint gets these roles without presenting a key. Create a key, or give {NameRules.McpUserName} only {BuiltInRoles.ReaderName}."
                : null;
    }

    private static bool IsNamed(UserSnapshot user, string userName) =>
        string.Equals(user.UserName, userName, StringComparison.Ordinal);

    private static string DescribeRoles(IReadOnlyCollection<string> roles) => string.Join(", ", roles);

    /// <summary>Removes the user and their external logins.</summary>
    [Operation(Title = "Remove", Description = "Ends the sessions of the user and deletes them with their external sign-ins.", Icon = "Delete", RequiresAdministration = true, RequiresConfirmation = true)]
    public async Task RemoveAsync([FromServices] IUserAdministration administration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administration);

        ThrowIfRefused(await administration.RemoveAsync(UserName, cancellationToken));
    }

    /// <summary>Removes the password, ends the user's sessions and returns a one-time setup code.</summary>
    [Operation(Title = "Reset Password", Description = "Removes the password of the user and ends their sessions; they set a new one with a one-time setup code, shown next.", Icon = "LockReset", RequiresAdministration = true, RequiresConfirmation = true)]
    public async Task<OneTimeSecret> ResetPasswordAsync([FromServices] IUserAdministration administration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administration);

        var result = await administration.ResetPasswordAsync(UserName, cancellationToken);
        ThrowIfRefused(result);
        return Authorization.CreateSetupCodeSecret(UserName, result.SetupCode!);
    }

    /// <summary>Locks the user until unlocked and ends their sessions.</summary>
    [Operation(Title = "Lock", Icon = "Lock", RequiresAdministration = true)]
    public async Task LockAsync([FromServices] IUserAdministration administration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administration);

        ThrowIfRefused(await administration.SetLockedAsync(UserName, true, cancellationToken));
    }

    /// <summary>Unlocks the user.</summary>
    [Operation(Title = "Unlock", Icon = "LockOpen", RequiresAdministration = true)]
    public async Task UnlockAsync([FromServices] IUserAdministration administration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administration);

        ThrowIfRefused(await administration.SetLockedAsync(UserName, false, cancellationToken));
    }

    /// <summary>Replaces the roles of the user; their open sessions keep going with the new roles.</summary>
    [Operation(Title = "Set Roles", Description = "Replaces the roles of the user. Only the roles you hold can be assigned.", Icon = "Badge", RequiresAdministration = true)]
    public async Task SetRolesAsync(
        [FromServices] IUserAdministration administration,
        [OperationParameter(Editor = AuthorizationParameterEditors.RoleNames, InitialValueProperty = nameof(Roles))] string[] roles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administration);
        ArgumentNullException.ThrowIfNull(roles);

        ThrowIfRefused(await administration.SetRolesAsync(UserName, roles, cancellationToken));
    }

    /// <summary>Sets the name shown for the user in place of their user name; an empty one removes it. Their sessions keep going.</summary>
    [Operation(Title = "Set Display Name", Description = "Sets the name shown for the user in place of their user name, such as their full name. An empty name removes it.", Icon = "DriveFileRenameOutline", RequiresAdministration = true)]
    public async Task SetDisplayNameAsync(
        [FromServices] IUserAdministration administration,
        [OperationParameter(InitialValueProperty = nameof(DisplayName))] string? displayName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administration);

        ThrowIfRefused(await administration.SetDisplayNameAsync(UserName, displayName, cancellationToken));
    }

    /// <summary>
    /// Creates the key that clients of the external MCP endpoint must then present, replacing an earlier one, and returns it
    /// once. Only for the built-in user <see cref="NameRules.McpUserName"/>.
    /// </summary>
    [Operation(Title = "Create Key", Description = "Clients of the external MCP endpoint then have to present the key, which is shown next, once. A new key replaces the current one, which clients then can no longer use.", Icon = "Key", RequiresAdministration = true, RequiresConfirmation = true)]
    public async Task<OneTimeSecret> CreateKeyAsync([FromServices] IUserAdministration administration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administration);

        ThrowIfNotMcpUser();
        var result = await administration.CreateMcpKeyAsync(cancellationToken);
        ThrowIfRefused(result);
        return new OneTimeSecret(
            $"Key of {NameRules.McpUserName}",
            "Clients of the external MCP endpoint must now present this key as a bearer token. It is shown only now; creating a key again replaces it.",
            result.McpKey!);
    }

    /// <summary>
    /// Removes the key, after which clients of the external MCP endpoint present none. Only for the built-in user
    /// <see cref="NameRules.McpUserName"/>.
    /// </summary>
    [Operation(Title = "Revoke Key", Description = "Without a key, every client that reaches the external MCP endpoint gets the roles of the built-in user " + NameRules.McpUserName + ".", Icon = "KeyOff", RequiresAdministration = true, RequiresConfirmation = true)]
    public async Task RevokeKeyAsync([FromServices] IUserAdministration administration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administration);

        ThrowIfNotMcpUser();
        ThrowIfRefused(await administration.RevokeMcpKeyAsync(cancellationToken));
    }

    internal static void ThrowIfRefused(UserChangeResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Error);
        }
    }

    private void ThrowIfNotMcpUser()
    {
        if (!IsMcpUser)
        {
            throw new InvalidOperationException($"Only the built-in user '{NameRules.McpUserName}' has a key.");
        }
    }
}
