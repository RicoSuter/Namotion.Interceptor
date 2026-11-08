using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>How a <see cref="SetupCodeSignIn"/> ended.</summary>
internal enum SetupCodeOutcome
{
    /// <summary>The code is spent and the person is signed in, with their new password unless storing it failed.</summary>
    SignedIn,

    /// <summary>The new password is refused before the code is checked; the errors say why.</summary>
    InvalidPassword,

    /// <summary>The user or the code is refused, see <see cref="SignInRefusals.Message"/>.</summary>
    Refused
}

/// <summary>
/// Redeems the one-time setup code of a new or reset user, stores the password they choose and signs them in. Scoped per
/// request.
/// </summary>
internal sealed class SetupCodeSignIn(
    AuthenticationDbContext database,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    UserStore userStore,
    TimeProvider timeProvider,
    ILogger<SetupCodeSignIn> logger)
{
    // The password policy is checked against no real user, so that its errors are the same whether or not the user exists.
    private static readonly ApplicationUser PolicyUser = new();

    /// <summary>
    /// Redeems <paramref name="code"/> for the user named <paramref name="userName"/>, stores <paramref name="newPassword"/>
    /// and signs them in. The password is checked first, so that a refused password spends no code.
    /// </summary>
    public async Task<(SetupCodeOutcome Outcome, IReadOnlyList<string> Errors)> RedeemAsync(
        HttpContext context, string userName, string code, string newPassword, string confirmPassword)
    {
        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return (SetupCodeOutcome.InvalidPassword, [PasswordChange.MismatchError]);
        }

        var policyErrors = await GetPolicyErrorsAsync(newPassword);
        if (policyErrors.Count > 0)
        {
            return (SetupCodeOutcome.InvalidPassword, policyErrors);
        }

        var user = await SignInRefusals.FindUserAsync(userManager, userName);
        if (user is null || user.IsBuiltIn)
        {
            SetupCodes.RefuseWithoutUser(code);
            await SignInRefusals.LogAsync(logger, userManager, userStore, context, user, wasLockedOut: false);
            return (SetupCodeOutcome.Refused, [SignInRefusals.Message]);
        }

        var wasLockedOut = await userManager.IsLockedOutAsync(user);
        if (!await SetupCodes.TryRedeemAsync(userManager, user, code, timeProvider))
        {
            await SignInRefusals.LogAsync(logger, userManager, userStore, context, user, wasLockedOut);
            return (SetupCodeOutcome.Refused, [SignInRefusals.Message]);
        }

        AuthenticationEvents.SetupCodeRedeemed(logger, user.UserName ?? user.Id);
        if (!await StorePasswordAsync(user, newPassword))
        {
            // Signed in with the required password change, as the code is spent: they set the password in the app.
            // In memory only: the stored flag is already true for every user that holds a code, and a persisted write would need a stamp renewal.
            user.MustChangePassword = true;
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        await userStore.RecordSignInAsync(user.Id, context.RequestAborted);
        AuthenticationEvents.SignInSucceeded(logger, user.UserName ?? user.Id, SignInRefusals.GetRemoteAddress(context));
        return (SetupCodeOutcome.SignedIn, []);
    }

    private async Task<bool> StorePasswordAsync(ApplicationUser user, string newPassword)
    {
        var result = await userManager.HasPasswordAsync(user)
            ? IdentityResult.Failed(new IdentityError { Code = "HasPassword", Description = "The user has a password." })
            : await PasswordChange.AddPasswordAsync(database, userManager, user, newPassword);
        if (result.Succeeded)
        {
            AuthenticationEvents.PasswordChanged(logger, user.UserName ?? user.Id);
            return true;
        }

        logger.LogWarning(
            "Could not {Step} of user {UserId}: {Errors}", "store the password chosen with the setup code", user.Id,
            string.Join(", ", result.Errors.Select(error => error.Code)));
        return false;
    }

    private async Task<IReadOnlyList<string>> GetPolicyErrorsAsync(string newPassword)
    {
        var errors = new List<string>();
        foreach (var validator in userManager.PasswordValidators)
        {
            var result = await validator.ValidateAsync(userManager, PolicyUser, newPassword);
            errors.AddRange(result.Errors.Select(error => error.Description));
        }

        return errors;
    }
}
