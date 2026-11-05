using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Identity;

namespace HomeBlaze.Authentication;

/// <summary>
/// The optional key of the built-in user <see cref="NameRules.McpUserName"/>, which clients of the external MCP endpoint
/// present as a bearer token. Only its hash is stored, in Identity's user tokens. Changed only through the
/// <see cref="UserStore"/>, which holds the hash that requests are checked against.
/// </summary>
internal static class McpClientKey
{
    internal const string LoginProvider = "HomeBlaze";
    internal const string TokenName = "McpKey";

    private const string Prefix = "hb_";

    /// <summary>Creates a key for <paramref name="mcpUser"/>, replacing an earlier one, and returns it; it is never stored.</summary>
    /// <exception cref="InvalidOperationException">The key could not be stored; the message says why.</exception>
    public static async Task<string> CreateAsync(UserManager<ApplicationUser> userManager, ApplicationUser mcpUser)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(mcpUser);

        var key = Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        ThrowIfFailed(await userManager.SetAuthenticationTokenAsync(mcpUser, LoginProvider, TokenName, Hash(key)));
        return key;
    }

    /// <summary>Removes the key of <paramref name="mcpUser"/>, if any.</summary>
    /// <exception cref="InvalidOperationException">The key could not be removed; the message says why.</exception>
    public static async Task RevokeAsync(UserManager<ApplicationUser> userManager, ApplicationUser mcpUser)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(mcpUser);

        ThrowIfFailed(await userManager.RemoveAuthenticationTokenAsync(mcpUser, LoginProvider, TokenName));
    }

    /// <summary>Gets whether <paramref name="mcpUser"/> has a key.</summary>
    public static async Task<bool> HasKeyAsync(UserManager<ApplicationUser> userManager, ApplicationUser mcpUser)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(mcpUser);

        return await userManager.GetAuthenticationTokenAsync(mcpUser, LoginProvider, TokenName) is not null;
    }

    /// <summary>Gets whether <paramref name="presented"/> is the key of <paramref name="mcpUser"/>; false when it has none.</summary>
    public static async Task<bool> VerifyAsync(UserManager<ApplicationUser> userManager, ApplicationUser mcpUser, string presented)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(mcpUser);
        ArgumentNullException.ThrowIfNull(presented);

        return Matches(await userManager.GetAuthenticationTokenAsync(mcpUser, LoginProvider, TokenName), presented);
    }

    /// <summary>
    /// Gets whether <paramref name="presented"/> hashes to <paramref name="storedHash"/>, compared in constant time; false
    /// when nothing is stored.
    /// </summary>
    internal static bool Matches(string? storedHash, string presented) =>
        storedHash is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(Hash(presented)));

    /// <summary>Gets the stored form of <paramref name="key"/>.</summary>
    internal static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(error => error.Description)));
        }
    }
}
