using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>Colours of the theme of a page.</summary>
internal static class PageColours
{
    /// <summary>
    /// Gets the computed colour of a CSS colour value, such as a palette variable, in the form the browser reports for an element.
    /// </summary>
    public static Task<string> ResolveColourAsync(this IPage page, string value)
    {
        return page.EvaluateAsync<string>(
            """
            value => {
                const probe = document.createElement('span');
                probe.style.color = value;
                document.body.appendChild(probe);
                const colour = getComputedStyle(probe).color;
                probe.remove();
                return colour;
            }
            """,
            value);
    }
}
