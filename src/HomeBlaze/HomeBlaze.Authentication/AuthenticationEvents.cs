using Microsoft.Extensions.Logging;

namespace HomeBlaze.Authentication;

/// <summary>
/// The structured log events of sign-ins and of changes to users, roles and the key of the MCP user, the forensics an
/// operator has. They never carry passwords, setup codes or keys.
/// </summary>
public static partial class AuthenticationEvents
{
    /// <summary>A person signed in.</summary>
    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "The user {UserName} signed in from {RemoteAddress}.")]
    public static partial void SignInSucceeded(ILogger logger, string userName, string remoteAddress);

    /// <summary>A sign-in was refused.</summary>
    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "A sign-in as {UserName} from {RemoteAddress} failed.")]
    public static partial void SignInFailed(ILogger logger, string userName, string remoteAddress);

    /// <summary>A user was locked out after failed sign-ins.</summary>
    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "The user {UserName} is locked out after failed sign-ins.")]
    public static partial void LockedOut(ILogger logger, string userName);

    /// <summary>A person changed their password.</summary>
    [LoggerMessage(EventId = 3004, Level = LogLevel.Information, Message = "The user {UserName} changed their password.")]
    public static partial void PasswordChanged(ILogger logger, string userName);

    /// <summary>A setup code was created for a user.</summary>
    [LoggerMessage(EventId = 3005, Level = LogLevel.Information, Message = "A setup code for {UserName} was created by {Actor}.")]
    public static partial void SetupCodeCreated(ILogger logger, string userName, string actor);

    /// <summary>A user set their password with a setup code.</summary>
    [LoggerMessage(EventId = 3006, Level = LogLevel.Information, Message = "The user {UserName} used their setup code.")]
    public static partial void SetupCodeRedeemed(ILogger logger, string userName);

    /// <summary>A user was added, removed, reset, locked, unlocked or given other roles.</summary>
    [LoggerMessage(EventId = 3007, Level = LogLevel.Information, Message = "The user {UserName} was changed by {Actor}: {Change}.")]
    public static partial void UserChanged(ILogger logger, string userName, string change, string actor);

    /// <summary>A custom role was created, changed, renamed or deleted.</summary>
    [LoggerMessage(EventId = 3008, Level = LogLevel.Information, Message = "The role {RoleName} was changed by {Actor}: {Change}.")]
    public static partial void RoleChanged(ILogger logger, string roleName, string change, string actor);

    /// <summary>The key of the MCP user was created or revoked.</summary>
    [LoggerMessage(EventId = 3009, Level = LogLevel.Information, Message = "The key of the MCP user was changed by {Actor}: {Change}.")]
    public static partial void McpKeyChanged(ILogger logger, string change, string actor);
}
