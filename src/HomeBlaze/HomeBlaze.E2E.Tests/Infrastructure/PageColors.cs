using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>Colors of the theme of a page.</summary>
internal static class PageColors
{
    /// <summary>
    /// Gets the computed color of a CSS color value, such as a palette variable, in the form the browser reports for an element.
    /// </summary>
    public static Task<string> ResolveColorAsync(this IPage page, string value)
    {
        return page.EvaluateAsync<string>(
            """
            value => {
                const probe = document.createElement('span');
                probe.style.color = value;
                document.body.appendChild(probe);
                const color = getComputedStyle(probe).color;
                probe.remove();
                return color;
            }
            """,
            value);
    }
}
