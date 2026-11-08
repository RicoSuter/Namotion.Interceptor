using System.Text;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// What the sign-in endpoints share: finding the user of a typed name, and, when they refuse an attempt, one message, the
/// same work whether or not the user exists, and the log events.
/// </summary>
internal static class SignInRefusals
{
    /// <summary>The one message of every refused sign-in, so that it reveals neither whether a user exists nor whether they are locked out.</summary>
    public const string Message = "The user name or password is wrong, or the account is locked. Try again later.";

    // Logged in place of a typed name that matches no user, which may be a password typed into the wrong field.
    private const string UnknownUserName = "(unknown user)";

    private static readonly ApplicationUser DummyUser = new();

    // Created once with the hasher of the first refusal; a benign race creates it twice at most.
    private static string? _dummyPasswordHash;

    /// <summary>
    /// Finds the user named <paramref name="typedUserName"/> trimmed and lowercased, compared ordinally; null when the
    /// typed name holds a character outside ASCII, breaks the <see cref="NameRules"/> or names no user.
    /// </summary>
    public static async Task<ApplicationUser?> FindUserAsync(UserManager<ApplicationUser> userManager, string typedUserName)
    {
        // Identity finds a name ignoring case, which maps some letters outside ASCII, such as the Kelvin sign, to ASCII ones.
        var trimmed = typedUserName.Trim();
        if (!Ascii.IsValid(trimmed))
        {
            return null;
        }

        var userName = trimmed.ToLowerInvariant();
        if (NameRules.GetNameError(userName, "user name") is not null)
        {
            return null;
        }

        var user = await userManager.FindByNameAsync(userName);
        return string.Equals(user?.UserName, userName, StringComparison.Ordinal) ? user : null;
    }

    /// <summary>Gets the address of the client of <paramref name="context"/> for the log.</summary>
    public static string GetRemoteAddress(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Verifies <paramref name="password"/> against a fixed hash, so that refusing an attempt without verifying a stored
    /// password, such as for a user who does not exist, takes as long as refusing a wrong password.
    /// </summary>
    public static void VerifyDummyPassword(IPasswordHasher<ApplicationUser> passwordHasher, string password)
    {
        var dummyHash = _dummyPasswordHash ??= passwordHasher.HashPassword(DummyUser, Guid.NewGuid().ToString());
        _ = passwordHasher.VerifyHashedPassword(DummyUser, dummyHash, password);
    }

    /// <summary>
    /// Logs a refused attempt from <paramref name="context"/> for <paramref name="user"/>, null when no user has the
    /// typed name, and, when the attempt started a lockout of the user, logs it and shows it in
    /// <paramref name="userStore"/>. <paramref name="wasLockedOut"/> says whether the user was locked out before.
    /// </summary>
    public static async Task LogAsync(
        ILogger logger, UserManager<ApplicationUser> userManager, UserStore userStore, HttpContext context, ApplicationUser? user, bool wasLockedOut)
    {
        if (user is { IsBuiltIn: false } && !wasLockedOut && await userManager.IsLockedOutAsync(user))
        {
            await LogLockoutAsync(logger, userStore, user);
        }

        AuthenticationEvents.SignInFailed(logger, user?.UserName ?? UnknownUserName, GetRemoteAddress(context));
    }

    /// <summary>Logs that <paramref name="user"/> is locked out now and shows it in <paramref name="userStore"/>.</summary>
    public static async Task LogLockoutAsync(ILogger logger, UserStore userStore, ApplicationUser user)
    {
        AuthenticationEvents.LockedOut(logger, user.UserName ?? user.Id);
        await userStore.RefreshUserAsync(user.Id, CancellationToken.None);
    }
}
