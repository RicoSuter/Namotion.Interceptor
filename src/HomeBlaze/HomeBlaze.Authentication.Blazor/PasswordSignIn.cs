using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// Signs a person in with their user name and password, or the seeded or reset administrator with the empty password
/// until they set one. Every refusal looks the same, see <see cref="SignInRefusals"/>. Scoped per request.
/// </summary>
internal sealed class PasswordSignIn(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IPasswordHasher<ApplicationUser> passwordHasher,
    UserStore userStore,
    ILogger<PasswordSignIn> logger)
{
    /// <summary>
    /// Signs in the user named <paramref name="userName"/> with <paramref name="password"/> from the request
    /// <paramref name="context"/>, with a session that outlives the browser when <paramref name="isPersistent"/>, never
    /// for the empty password, and returns whether it succeeded.
    /// </summary>
    // Every refusal verifies one password hash, a stored or a dummy one, so that the hash work is the same whether or not
    // the user exists, has a password or is locked out. Only that work is equal: a wrong password for an existing user
    // also stores the failed access, which an unknown name does not.
    public async Task<bool> SignInAsync(HttpContext context, string userName, string password, bool isPersistent)
    {
        var user = await SignInRefusals.FindUserAsync(userManager, userName);
        if (user is null || user.IsBuiltIn)
        {
            SignInRefusals.VerifyDummyPassword(passwordHasher, password);
            await RefuseAsync(context, user, wasLockedOut: false);
            return false;
        }

        var wasLockedOut = await userManager.IsLockedOutAsync(user);
        if (password.Length == 0)
        {
            return await SignInWithEmptyPasswordAsync(context, user, wasLockedOut);
        }

        // Identity verifies no hash for a user without a password or locked out.
        if (user.PasswordHash is null || wasLockedOut)
        {
            SignInRefusals.VerifyDummyPassword(passwordHasher, password);
        }

        var result = await signInManager.PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            await SignedInAsync(context, user);
            return true;
        }

        await RefuseAsync(context, user, wasLockedOut);
        return false;
    }

    // Only the seeded or reset administrator, until they set a password. The store confines the session to setting it, see
    // UserStore.RequiresPasswordChange.
    private async Task<bool> SignInWithEmptyPasswordAsync(HttpContext context, ApplicationUser user, bool wasLockedOut)
    {
        if (!user.AllowsEmptyPassword || user.PasswordHash is not null || wasLockedOut)
        {
            SignInRefusals.VerifyDummyPassword(passwordHasher, string.Empty);
            await RefuseAsync(context, user, wasLockedOut);
            return false;
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        await SignedInAsync(context, user);
        return true;
    }

    private async Task SignedInAsync(HttpContext context, ApplicationUser user)
    {
        await userStore.RecordSignInAsync(user.Id, CancellationToken.None);
        AuthenticationEvents.SignInSucceeded(logger, user.UserName ?? user.Id, SignInRefusals.GetRemoteAddress(context));
    }

    private Task RefuseAsync(HttpContext context, ApplicationUser? user, bool wasLockedOut) =>
        SignInRefusals.LogAsync(logger, userManager, userStore, context, user, wasLockedOut);
}
