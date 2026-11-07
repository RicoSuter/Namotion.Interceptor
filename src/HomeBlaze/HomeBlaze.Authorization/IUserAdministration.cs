using System.Globalization;
using System.Text;

namespace HomeBlaze.Authorization;

/// <summary>
/// Changes the users of the authentication database as the acting person, under the rules for managing users: the
/// person's policy must allow <c>administer</c> on the user (or on adding users), and a person who is not a full
/// administrator assigns only roles they hold and never acts on themselves, on a user holding a role they do not hold,
/// or on a built-in user. A refused change leaves everything unchanged and reports the reason.
/// </summary>
public interface IUserAdministration
{
    /// <summary>Adds a user without a password, holding <paramref name="roles"/>, and creates their setup code.</summary>
    Task<UserChangeResult> AddUserAsync(string userName, string? email, IReadOnlyCollection<string> roles, CancellationToken cancellationToken);

    /// <summary>Removes the user and their external logins.</summary>
    Task<UserChangeResult> RemoveAsync(string userName, CancellationToken cancellationToken);

    /// <summary>Removes the user's password, ends their sessions and creates a setup code.</summary>
    Task<UserChangeResult> ResetPasswordAsync(string userName, CancellationToken cancellationToken);

    /// <summary>Locks the user until unlocked, ending their sessions, or unlocks them.</summary>
    Task<UserChangeResult> SetLockedAsync(string userName, bool isLocked, CancellationToken cancellationToken);

    /// <summary>Replaces the roles of the user with <paramref name="roles"/>; their open sessions keep going with the new roles.</summary>
    Task<UserChangeResult> SetRolesAsync(string userName, IReadOnlyCollection<string> roles, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the display name of the user, see <see cref="NameRules.GetDisplayNameError"/>; an empty one removes it. Their
    /// open sessions keep going. Built-in users have none.
    /// </summary>
    Task<UserChangeResult> SetDisplayNameAsync(string userName, string? displayName, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the key of the built-in user <see cref="NameRules.McpUserName"/>, replacing an earlier one, which clients of
    /// the external MCP endpoint must then present. Only a full administrator may.
    /// </summary>
    Task<UserChangeResult> CreateMcpKeyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes the key of the built-in user <see cref="NameRules.McpUserName"/>, after which clients of the external MCP
    /// endpoint present none. Only a full administrator may.
    /// </summary>
    Task<UserChangeResult> RevokeMcpKeyAsync(CancellationToken cancellationToken);
}

/// <summary>The outcome of a change of <see cref="IUserAdministration"/>.</summary>
/// <param name="Succeeded">Whether the change was committed.</param>
/// <param name="Error">Why the change was refused; null when it succeeded.</param>
/// <param name="SetupCode">The one-time setup code of an added or reset user, shown once; otherwise null.</param>
/// <param name="McpKey">The created key of the built-in user <see cref="NameRules.McpUserName"/>, shown once; otherwise null.</param>
public readonly record struct UserChangeResult(bool Succeeded, string? Error, string? SetupCode = null, string? McpKey = null)
{
    /// <summary>How long a setup code is valid after it was created.</summary>
    public static TimeSpan SetupCodeLifetime { get; } = TimeSpan.FromHours(24);

    // The secrets never reach a log or a debugger display through ToString.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Succeeded = {Succeeded}, Error = {Error}, HasSetupCode = {SetupCode is not null}, HasMcpKey = {McpKey is not null}");
        return true;
    }
}
