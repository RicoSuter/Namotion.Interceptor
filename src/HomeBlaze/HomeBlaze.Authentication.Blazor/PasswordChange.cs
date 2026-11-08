using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>How a <see cref="PasswordChange"/> ended.</summary>
internal enum PasswordChangeOutcome
{
    /// <summary>The password is stored, and the session is signed in again with the new security stamp.</summary>
    Changed,

    /// <summary>The password is not stored; the errors say why.</summary>
    Refused,

    /// <summary>The session was ended, or ended now: it is signed out and nothing is stored.</summary>
    SignedOut
}

/// <summary>
/// Sets the first password of the signed-in person, or changes it with their current password, and ends their other
/// sessions. Scoped per request.
/// </summary>
internal sealed class PasswordChange(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    UserStore userStore,
    ILogger<PasswordChange> logger)
{
    /// <summary>The error of new passwords that differ.</summary>
    public const string MismatchError = "The new passwords do not match.";

    /// <summary>The error while the person is locked out after failed attempts with their password.</summary>
    public const string LockedOutError = "Too many wrong passwords. Try again in a few minutes.";

    /// <summary>
    /// Stores <paramref name="newPassword"/> for the person of <paramref name="context"/>, verifying
    /// <paramref name="currentPassword"/> when they have a password, and returns the outcome and the errors of a refusal.
    /// </summary>
    public async Task<(PasswordChangeOutcome Outcome, IReadOnlyList<string> Errors)> ChangeAsync(
        HttpContext context, string currentPassword, string newPassword, string confirmPassword)
    {
        // The cookie is checked against the stored stamp only every few minutes, so a session that a reset or a lock
        // ended would otherwise still set the first password of a reset user.
        var user = await signInManager.ValidateSecurityStampAsync(context.User);
        if (user is null || user.IsLockedByAdministrator())
        {
            return await SignOutAsync();
        }

        // A lockout ends no session, so another session of the user could keep guessing the current password; it is
        // refused without signing the session out, as anyone can start the lockout by failing to sign in as the user.
        // Without a current password there is nothing to guess, so the first password can still be set.
        var hasPassword = await userManager.HasPasswordAsync(user);
        if (hasPassword && await userManager.IsLockedOutAsync(user))
        {
            return (PasswordChangeOutcome.Refused, [LockedOutError]);
        }

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return (PasswordChangeOutcome.Refused, [MismatchError]);
        }

        var result = await StorePasswordAsync(userManager, user, hasPassword, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            return await RefuseAsync(context, user, result);
        }

        await signInManager.RefreshSignInAsync(user);
        await userStore.RefreshUserAsync(user.Id, context.RequestAborted);
        AuthenticationEvents.PasswordChanged(logger, user.UserName ?? user.Id);
        return (PasswordChangeOutcome.Changed, []);
    }

    /// <summary>
    /// Adds <paramref name="newPassword"/> to <paramref name="user"/>, who has none, clears the required password change
    /// and the empty password, and renews the security stamp, which ends the other sessions of the user.
    /// </summary>
    public static Task<IdentityResult> AddPasswordAsync(UserManager<ApplicationUser> userManager, ApplicationUser user, string newPassword) =>
        StorePasswordAsync(userManager, user, hasPassword: false, currentPassword: string.Empty, newPassword);

    private static async Task<IdentityResult> StorePasswordAsync(
        UserManager<ApplicationUser> userManager, ApplicationUser user, bool hasPassword, string currentPassword, string newPassword)
    {
        var result = hasPassword
            ? await userManager.ChangePasswordAsync(user, currentPassword, newPassword)
            : await userManager.AddPasswordAsync(user, newPassword);
        if (!result.Succeeded)
        {
            return result;
        }

        user.MustChangePassword = false;
        user.AllowsEmptyPassword = false;
        // Also stores both flags, and ends the other sessions of the user.
        return await userManager.UpdateSecurityStampAsync(user);
    }

    // A wrong current password counts as a failed sign-in. The lockout it starts is refused as one found before the
    // attempt, without signing out, as the failed sign-ins of anyone else may have counted towards it.
    private async Task<(PasswordChangeOutcome Outcome, IReadOnlyList<string> Errors)> RefuseAsync(HttpContext context, ApplicationUser user, IdentityResult result)
    {
        IReadOnlyList<string> errors = [.. result.Errors.Select(error => error.Description)];
        if (!result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
        {
            return (PasswordChangeOutcome.Refused, errors);
        }

        var counted = await userManager.AccessFailedAsync(user);
        if (!counted.Succeeded)
        {
            logger.LogWarning(
                "Could not {Step} of user {UserId}: {Errors}", "count the failed access", user.Id, string.Join(", ", counted.Errors.Select(error => error.Code)));
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            await SignInRefusals.LogLockoutAsync(logger, userStore, context, user);
            return (PasswordChangeOutcome.Refused, [LockedOutError]);
        }

        return (PasswordChangeOutcome.Refused, errors);
    }

    private async Task<(PasswordChangeOutcome Outcome, IReadOnlyList<string> Errors)> SignOutAsync()
    {
        await signInManager.SignOutAsync();
        return (PasswordChangeOutcome.SignedOut, []);
    }
}
