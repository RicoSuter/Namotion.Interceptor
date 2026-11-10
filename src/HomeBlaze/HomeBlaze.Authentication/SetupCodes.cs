using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Authentication;

/// <summary>
/// One-time codes with which a new or reset user signs in once to set a password. Only a hash and the expiry are
/// stored, in Identity's user tokens.
/// </summary>
public static class SetupCodes
{
    private const string LoginProvider = "HomeBlaze";
    private const string TokenName = "SetupCode";

    private static readonly byte[] DummyHash = Encoding.ASCII.GetBytes(Hash(string.Empty));

    /// <summary>How long a code is valid.</summary>
    public static TimeSpan Lifetime => UserChangeResult.SetupCodeLifetime;

    /// <summary>Creates a code for <paramref name="user"/>, replacing an earlier one, and requires a password change.</summary>
    /// <exception cref="InvalidOperationException">The code or the password change could not be stored; the message says why.</exception>
    public static async Task<string> CreateAsync(UserManager<ApplicationUser> userManager, ApplicationUser user, TimeProvider timeProvider)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        var expiry = timeProvider.GetUtcNow().Add(Lifetime).UtcTicks.ToString(CultureInfo.InvariantCulture);
        ThrowIfFailed(await userManager.SetAuthenticationTokenAsync(user, LoginProvider, TokenName, $"{Hash(code)}|{expiry}"));
        user.MustChangePassword = true;
        ThrowIfFailed(await userManager.UpdateAsync(user));
        return code;
    }

    /// <summary>
    /// Redeems <paramref name="code"/>, ignoring case and surrounding white space: true and the code is spent when it
    /// matches and has not expired; otherwise false, counted as a failed access for lockout. Always false while the user
    /// is locked out.
    /// </summary>
    /// <remarks>
    /// The lockout end is compared with <paramref name="timeProvider"/>, like the expiry, but Identity sets it from the
    /// system clock, which it offers no way to replace.
    /// </remarks>
    public static async Task<bool> TryRedeemAsync(UserManager<ApplicationUser> userManager, ApplicationUser user, string code, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(code);

        var now = timeProvider.GetUtcNow();
        if (user.LockoutEnabled && user.LockoutEnd > now)
        {
            return false;
        }

        var stored = await userManager.GetAuthenticationTokenAsync(user, LoginProvider, TokenName);
        var parts = stored?.Split('|');
        var isValid = parts is { Length: 2 } &&
            long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
            now.UtcTicks <= ticks &&
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[0]), Encoding.ASCII.GetBytes(Hash(code.Trim().ToUpperInvariant())));

        if (!isValid)
        {
            LogIfFailed(userManager, user, await userManager.AccessFailedAsync(user), "count the failed access");
            return false;
        }

        // The count is reset before the code is spent, so that neither failing strands the user with a spent code.
        if (LogIfFailed(userManager, user, await userManager.ResetAccessFailedCountAsync(user), "reset the failed access count") ||
            LogIfFailed(userManager, user, await userManager.RemoveAuthenticationTokenAsync(user, LoginProvider, TokenName), "spend the setup code"))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Hashes <paramref name="code"/> and compares it in fixed time with a dummy hash, as <see cref="TryRedeemAsync"/>
    /// does with the stored one, so that refusing a code for a user who does not exist takes the same work as refusing
    /// a wrong code. Always false.
    /// </summary>
    /// <remarks>
    /// Only the hash work is equal: a wrong code for an existing user also stores the failed access, which a caller
    /// cannot make equal.
    /// </remarks>
    public static bool RefuseWithoutUser(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        _ = CryptographicOperations.FixedTimeEquals(DummyHash, Encoding.ASCII.GetBytes(Hash(code.Trim().ToUpperInvariant())));
        return false;
    }

    // Logs the user id and Identity's error codes, never the code or its hash. Returns whether the result failed.
    private static bool LogIfFailed(UserManager<ApplicationUser> userManager, ApplicationUser user, IdentityResult result, string step)
    {
        if (result.Succeeded)
        {
            return false;
        }

        userManager.Logger.LogWarning(
            "Could not {Step} of user {UserId}: {Errors}", step, user.Id, string.Join(", ", result.Errors.Select(error => error.Code)));
        return true;
    }

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(error => error.Description)));
        }
    }

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
