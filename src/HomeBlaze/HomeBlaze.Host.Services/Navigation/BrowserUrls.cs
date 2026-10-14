using System.Text;
using HomeBlaze.Abstractions;

namespace HomeBlaze.Host.Services.Navigation;

/// <summary>
/// Builds the URLs of the subject browser.
/// </summary>
public static class BrowserUrls
{
    /// <summary>
    /// The URL of the browser at the root.
    /// </summary>
    public const string Root = "/browser";

    /// <summary>
    /// Builds the URL of the browser at the path of unescaped segments, such as property names and keys.
    /// </summary>
    /// <returns>The URL, with each segment escaped.</returns>
    public static string Create(IEnumerable<string> segments)
    {
        var url = new StringBuilder(Root);
        foreach (var segment in segments)
        {
            url.Append('/').Append(Uri.EscapeDataString(segment));
        }

        return url.ToString();
    }

    /// <summary>
    /// Builds the URL of the browser at a <see cref="PathStyle.Route"/> path, whose segments are separated by slashes.
    /// </summary>
    /// <returns>The URL, with each segment escaped.</returns>
    public static string FromRoutePath(string routePath) =>
        Create(routePath.Split('/', StringSplitOptions.RemoveEmptyEntries));
}
