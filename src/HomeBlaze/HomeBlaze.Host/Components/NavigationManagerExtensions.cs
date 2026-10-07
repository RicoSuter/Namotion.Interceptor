using Microsoft.AspNetCore.Components;

namespace HomeBlaze.Host.Components;

/// <summary>
/// Reads subject paths from URLs.
/// </summary>
public static class NavigationManagerExtensions
{
    /// <summary>
    /// Gets the path below a route prefix of a URL, without query and fragment, with its segments still escaped
    /// as in the URL. Returns an empty string for the prefix itself and null when the URL is not below it.
    /// </summary>
    /// <remarks>
    /// Pass the result to the path resolver, which unescapes each segment. A route parameter is already
    /// unescaped, so resolving it unescapes a key containing '%' twice and splits a key containing '/'.
    /// </remarks>
    /// <param name="navigationManager">The navigation manager whose base URI the URL is relative to.</param>
    /// <param name="uri">The absolute URL, for example <see cref="NavigationManager.Uri"/>.</param>
    /// <param name="prefix">The first path segment of the route, for example "pages".</param>
    public static string? GetEscapedPathBelow(this NavigationManager navigationManager, string uri, string prefix)
    {
        var relativePath = navigationManager.ToBaseRelativePath(uri);
        var end = relativePath.AsSpan().IndexOfAny('?', '#');
        if (end >= 0)
        {
            relativePath = relativePath[..end];
        }

        if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (relativePath.Length == prefix.Length)
        {
            return string.Empty;
        }

        return relativePath[prefix.Length] == '/'
            ? relativePath[(prefix.Length + 1)..].TrimEnd('/')
            : null;
    }
}
