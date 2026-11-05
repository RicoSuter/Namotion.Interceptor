using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Authentication.Internal;

/// <summary>
/// Reads and writes the users of the authentication database with Identity's scoped <see cref="UserManager{TUser}"/>,
/// in a scope per call. A write runs in one transaction and an error rolls all of it back.
/// </summary>
internal static class UserDatabase
{
    /// <summary>Reads every user with the names of their roles, and the stored hash of the key of the built-in user <see cref="NameRules.McpUserName"/>.</summary>
    public static async Task<UserState> LoadAsync(IServiceScopeFactory scopeFactory, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthenticationDbContext>();
        var users = await context.Users.AsNoTracking().ToListAsync(cancellationToken);
        var userRoles = await context.UserRoles.AsNoTracking()
            .Join(context.Roles, userRole => userRole.RoleId, role => role.Id, (userRole, role) => new { userRole.UserId, role.Name })
            .ToListAsync(cancellationToken);

        var rolesByUser = userRoles
            .Where(userRole => userRole.Name is not null)
            .GroupBy(userRole => userRole.UserId)
            .ToDictionary(group => group.Key, group => group.Select(userRole => userRole.Name!).Order(StringComparer.Ordinal).ToArray());

        var entries = users
            .Where(user => user.UserName is not null)
            .Select(user => new UserEntry
            {
                Id = user.Id,
                UserName = user.UserName!,
                DisplayName = user.DisplayName,
                Email = user.Email,
                Roles = Array.AsReadOnly(rolesByUser.GetValueOrDefault(user.Id) ?? []),
                LockoutEnd = user.LockoutEnd,
                MustChangePassword = user.MustChangePassword,
                AllowsEmptyPassword = user.AllowsEmptyPassword,
                LastLogin = user.LastLogin,
                IsBuiltIn = user.IsBuiltIn
            })
            .ToList();

        var mcpUserId = entries.FirstOrDefault(UserEntry.IsMcpUser)?.Id;
        var mcpKeyHash = mcpUserId is null
            ? null
            : await context.UserTokens.AsNoTracking()
                .Where(token => token.UserId == mcpUserId && token.LoginProvider == McpClientKey.LoginProvider && token.Name == McpClientKey.TokenName)
                .Select(token => token.Value)
                .FirstOrDefaultAsync(cancellationToken);

        return new UserState(entries, mcpKeyHash);
    }

    /// <summary>
    /// Gets <paramref name="user"/> with the lockout, the required password change, whether an empty password is allowed
    /// and the last sign-in as stored, after
    /// storing <paramref name="signInTime"/> as the last sign-in when it is not null; a refused write is logged.
    /// </summary>
    /// <returns>The refreshed user, or null when the user no longer exists.</returns>
    public static async Task<UserEntry?> RefreshUserAsync(
        IServiceScopeFactory scopeFactory, UserEntry user, DateTimeOffset? signInTime, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        cancellationToken.ThrowIfCancellationRequested();
        if (await userManager.FindByIdAsync(user.Id) is not { } stored)
        {
            return null;
        }

        if (signInTime is not null)
        {
            var lastLogin = stored.LastLogin;
            stored.LastLogin = signInTime;
            if (GetError(await userManager.UpdateAsync(stored)) is { } error)
            {
                userManager.Logger.LogWarning("Could not store the last sign-in of user {UserId}: {Error}", user.Id, error);
                stored.LastLogin = lastLogin;
            }
        }

        return user with
        {
            LockoutEnd = stored.LockoutEnd,
            MustChangePassword = stored.MustChangePassword,
            AllowsEmptyPassword = stored.AllowsEmptyPassword,
            LastLogin = stored.LastLogin
        };
    }

    /// <summary>Adds a user without a password holding <paramref name="roles"/>, with a setup code.</summary>
    /// <returns>The id of the user and the setup code, or why the database refused it.</returns>
    public static Task<CommitResult> AddUserAsync(
        IServiceScopeFactory scopeFactory, string userName, string? email, string[] roles, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var user = new ApplicationUser { UserName = userName, Email = email, MustChangePassword = true };
        return CommitAsync(scopeFactory, async userManager =>
        {
            var error = GetError(await userManager.CreateAsync(user));
            if (error is null && roles.Length > 0)
            {
                error = GetError(await userManager.AddToRolesAsync(user, roles));
            }

            return error is null ? await CreateSetupCodeAsync(userManager, user, timeProvider) with { UserId = user.Id } : new CommitResult(error);
        }, cancellationToken);
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the user with <paramref name="userId"/>, updating their security stamp, which
    /// ends their sessions, unless only their roles or their display name change.
    /// </summary>
    /// <returns>The setup code of a reset, or why the database refused the change.</returns>
    public static Task<CommitResult> ChangeUserAsync(
        IServiceScopeFactory scopeFactory, string userId, UserChange change, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        CommitAsync(scopeFactory, async userManager =>
        {
            if (await userManager.FindByIdAsync(userId) is not { } user)
            {
                return new CommitResult("The user no longer exists.");
            }

            return change.Action switch
            {
                UserAction.Remove => new CommitResult(await RemoveAsync(userManager, user)),
                UserAction.Reset => await ResetPasswordAsync(userManager, user, timeProvider),
                UserAction.Lock => new CommitResult(await SetLockedAsync(userManager, user, true)),
                UserAction.Unlock => new CommitResult(await SetLockedAsync(userManager, user, false)),
                UserAction.SetDisplayName => new CommitResult(await SetDisplayNameAsync(userManager, user, change.DisplayName)),
                _ => new CommitResult(await SetRolesAsync(userManager, user, change.Roles ?? []))
            };
        }, cancellationToken);

    /// <summary>Creates the key of the built-in user with <paramref name="userId"/>, replacing an earlier one, or removes it.</summary>
    /// <returns>The created key, or why the database refused the change.</returns>
    public static Task<CommitResult> SetMcpKeyAsync(IServiceScopeFactory scopeFactory, string userId, bool create, CancellationToken cancellationToken) =>
        CommitAsync(scopeFactory, async userManager =>
        {
            if (await userManager.FindByIdAsync(userId) is not { } user)
            {
                return new CommitResult("The user no longer exists.");
            }

            try
            {
                if (!create)
                {
                    await McpClientKey.RevokeAsync(userManager, user);
                    return new CommitResult(null);
                }

                return new CommitResult(null, McpKey: await McpClientKey.CreateAsync(userManager, user));
            }
            catch (InvalidOperationException exception)
            {
                return new CommitResult($"The key could not be stored: {exception.Message}");
            }
        }, cancellationToken);

    private static async Task<CommitResult> CommitAsync(
        IServiceScopeFactory scopeFactory, Func<UserManager<ApplicationUser>, Task<CommitResult>> steps, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthenticationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var result = await steps(userManager);
        if (result.Error is null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    // The stamp first, so that open sessions end, and the external logins with the user, so that a later user of the
    // same name inherits none.
    private static async Task<string?> RemoveAsync(UserManager<ApplicationUser> userManager, ApplicationUser user)
    {
        if (GetError(await userManager.UpdateSecurityStampAsync(user)) is { } error)
        {
            return error;
        }

        foreach (var login in await userManager.GetLoginsAsync(user))
        {
            if (GetError(await userManager.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey)) is { } loginError)
            {
                return loginError;
            }
        }

        return GetError(await userManager.DeleteAsync(user));
    }

    private static async Task<CommitResult> ResetPasswordAsync(UserManager<ApplicationUser> userManager, ApplicationUser user, TimeProvider timeProvider)
    {
        if (await userManager.HasPasswordAsync(user) && GetError(await userManager.RemovePasswordAsync(user)) is { } error)
        {
            return new CommitResult(error);
        }

        user.AllowsEmptyPassword = false;
        var setupCode = await CreateSetupCodeAsync(userManager, user, timeProvider);
        if (setupCode.Error is not null)
        {
            return setupCode;
        }

        return GetError(await userManager.UpdateSecurityStampAsync(user)) is { } stampError ? new CommitResult(stampError) : setupCode;
    }

    private static async Task<CommitResult> CreateSetupCodeAsync(UserManager<ApplicationUser> userManager, ApplicationUser user, TimeProvider timeProvider)
    {
        try
        {
            return new CommitResult(null, await SetupCodes.CreateAsync(userManager, user, timeProvider));
        }
        catch (InvalidOperationException exception)
        {
            return new CommitResult($"The setup code could not be stored: {exception.Message}");
        }
    }

    private static async Task<string?> SetLockedAsync(UserManager<ApplicationUser> userManager, ApplicationUser user, bool isLocked)
    {
        if (!await userManager.GetLockoutEnabledAsync(user) && GetError(await userManager.SetLockoutEnabledAsync(user, true)) is { } enableError)
        {
            return enableError;
        }

        var error = GetError(await userManager.SetLockoutEndDateAsync(user, isLocked ? ApplicationUser.AdministratorLockoutEnd : null));
        if (error is null && !isLocked)
        {
            error = GetError(await userManager.ResetAccessFailedCountAsync(user));
        }

        return error ?? GetError(await userManager.UpdateSecurityStampAsync(user));
    }

    // The stamp stays, so that open sessions keep going.
    private static async Task<string?> SetDisplayNameAsync(UserManager<ApplicationUser> userManager, ApplicationUser user, string? displayName)
    {
        user.DisplayName = displayName;
        return GetError(await userManager.UpdateAsync(user));
    }

    private static async Task<string?> SetRolesAsync(UserManager<ApplicationUser> userManager, ApplicationUser user, string[] roles)
    {
        var current = await userManager.GetRolesAsync(user);
        var removed = current.Where(role => !roles.Contains(role, StringComparer.Ordinal)).ToArray();
        var added = roles.Where(role => !current.Contains(role, StringComparer.Ordinal)).ToArray();
        var error = removed.Length > 0 ? GetError(await userManager.RemoveFromRolesAsync(user, removed)) : null;
        if (error is null && added.Length > 0)
        {
            error = GetError(await userManager.AddToRolesAsync(user, added));
        }

        // The stamp stays, so that open sessions keep going and take the new roles from the store at once.
        return error;
    }

    private static string? GetError(IdentityResult result) =>
        result.Succeeded ? null : string.Join(" ", result.Errors.Select(error => error.Description));
}

/// <summary>The outcome of a write of <see cref="UserDatabase"/>.</summary>
/// <param name="Error">Why the database refused the write; null when it was committed.</param>
/// <param name="SetupCode">The setup code the write created, or null.</param>
/// <param name="UserId">The id of the added user, or null.</param>
/// <param name="McpKey">The key the write created, or null.</param>
internal readonly record struct CommitResult(string? Error, string? SetupCode = null, string? UserId = null, string? McpKey = null)
{
    // The secrets never reach a log or a debugger display through ToString.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Error = {Error}, HasSetupCode = {SetupCode is not null}, UserId = {UserId}, HasMcpKey = {McpKey is not null}");
        return true;
    }
}
