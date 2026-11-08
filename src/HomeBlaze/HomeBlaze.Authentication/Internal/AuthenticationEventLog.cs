using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Authentication.Internal;

/// <summary>
/// Writes the <see cref="AuthenticationEvents"/> of committed changes to users, roles and the key of the MCP user, and
/// raises the change of a user with every failing handler logged.
/// </summary>
internal sealed partial class AuthenticationEventLog(ILogger logger)
{
    // The role store is not told who changes a role.
    private const string UnknownActor = "unknown";

    /// <summary>A log that writes nothing.</summary>
    public static AuthenticationEventLog Silent { get; } = new(NullLogger.Instance);

    public void UserAdded(string userName, IReadOnlyCollection<string> roleNames, string actor)
    {
        AuthenticationEvents.UserChanged(logger, userName, $"added with the roles {FormatRoles(roleNames)}", actor);
        AuthenticationEvents.SetupCodeCreated(logger, userName, actor);
    }

    public void UserChanged(string userName, UserChange change, IReadOnlyCollection<string>? roleNames, string actor)
    {
        var description = change.Action switch
        {
            UserAction.Remove => "removed",
            UserAction.Reset => "password reset",
            UserAction.Lock => "locked",
            UserAction.Unlock => "unlocked",
            UserAction.SetDisplayName => change.DisplayName is { } displayName ? $"display name set to '{displayName}'" : "display name removed",
            _ => $"roles set to {FormatRoles(roleNames ?? [])}"
        };

        AuthenticationEvents.UserChanged(logger, userName, description, actor);
        if (change.Action == UserAction.Reset)
        {
            AuthenticationEvents.SetupCodeCreated(logger, userName, actor);
        }
    }

    public void McpKeyChanged(bool isCreated, string actor) =>
        AuthenticationEvents.McpKeyChanged(logger, isCreated ? "created" : "revoked", actor);

    public void RoleSaved(string roleName, string? originalName, string? renamedFrom)
    {
        var description = (originalName, renamedFrom) switch
        {
            (null, _) => "created",
            (_, null) => "changed",
            _ => $"renamed from {renamedFrom}"
        };

        AuthenticationEvents.RoleChanged(logger, roleName, description, UnknownActor);
    }

    public void RoleDeleted(string roleName) => AuthenticationEvents.RoleChanged(logger, roleName, "deleted", UnknownActor);

    /// <summary>
    /// Invokes every handler of <paramref name="handlers"/> with <paramref name="userId"/>, logging the exception of each one
    /// that throws, so that a failing handler skips no other handler and never fails a committed change.
    /// </summary>
    public void RaiseUserChanged(Action<string>? handlers, string userId)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string>)handler)(userId);
            }
            catch (Exception exception)
            {
                LogUserChangedHandlerFailed(logger, exception);
            }
        }
    }

    public void RoleRenamedHandlerFailed(Exception exception) => LogRoleRenamedHandlerFailed(logger, exception);

    public void StampReloadFailed(string userId, Exception exception) => LogStampReloadFailed(logger, userId, exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "User {UserId} could not be reloaded after a session carried another security stamp; the session stays refused.")]
    private static partial void LogStampReloadFailed(ILogger logger, string userId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The users could not take the new name of a renamed role; the rename stays committed.")]
    private static partial void LogRoleRenamedHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A handler of a committed user change failed; the change stays committed.")]
    private static partial void LogUserChangedHandlerFailed(ILogger logger, Exception exception);

    private static string FormatRoles(IReadOnlyCollection<string> roleNames) =>
        roleNames.Count == 0 ? "none" : string.Join(", ", roleNames);
}
