using System.Diagnostics.CodeAnalysis;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>Checks the page a person returns to after signing in, so that a link cannot send them to another site.</summary>
public static class ReturnUrls
{
    /// <summary>
    /// Gets <paramref name="returnUrl"/> when it is a local path below <paramref name="pathBase"/>, ignoring case;
    /// otherwise the root of the application, <paramref name="pathBase"/> followed by <c>/</c>.
    /// </summary>
    /// <param name="returnUrl">The requested return URL, as the sign-in redirect gives it, including the path base.</param>
    /// <param name="pathBase">The path base of the request, empty or starting with <c>/</c> and without a trailing <c>/</c>.</param>
    public static string GetSafe(string? returnUrl, string pathBase)
    {
        ArgumentNullException.ThrowIfNull(pathBase);

        return IsLocal(returnUrl) && IsBelow(returnUrl, pathBase) ? returnUrl : pathBase + "/";
    }

    // A single leading slash: "//host" and "/\host" name another host, and browsers drop tabs and line breaks, which
    // would turn "/\t/host" into "//host".
    private static bool IsLocal([NotNullWhen(true)] string? returnUrl) =>
        returnUrl is ['/', ..] && !NamesHost(returnUrl) && !returnUrl.Any(char.IsControl);

    private static bool NamesHost(string returnUrl) => returnUrl is [_, '/' or '\\', ..];

    private static bool IsBelow(string returnUrl, string pathBase) =>
        pathBase.Length == 0 ||
        (returnUrl.StartsWith(pathBase, StringComparison.OrdinalIgnoreCase) &&
            (returnUrl.Length == pathBase.Length || returnUrl[pathBase.Length] is '/' or '?' or '#'));
}
