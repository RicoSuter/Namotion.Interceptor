using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Finds the cards of a page.
/// </summary>
internal static class CardExtensions
{
    /// <summary>
    /// The cards whose title is exactly the given text, including a card that holds such a card.
    /// </summary>
    public static ILocator GetCard(this IPage page, string title) =>
        page.GetByTestId("card").Filter(new() { Has = page.GetByTestId("card-title").GetByText(title, new() { Exact = true }) });
}
