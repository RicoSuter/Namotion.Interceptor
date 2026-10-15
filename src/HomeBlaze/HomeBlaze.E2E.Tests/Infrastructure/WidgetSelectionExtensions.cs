using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Selects the widgets of a page in edit mode, which shows the bar with the actions of the selected widget.
/// </summary>
internal static class WidgetSelectionExtensions
{
    /// <summary>
    /// Clicks the middle of the element, which selects the innermost widget there, and returns the bar with the
    /// actions of that widget.
    /// </summary>
    public static async Task<ILocator> SelectWidgetAsync(this ILocator element)
    {
        // A click before the page has rendered in edit mode would not select anything.
        await Assertions.Expect(element.Page.Locator(".subject-component-wrapper.selectable").First).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(element).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Forced, because in edit mode the content of a widget takes no pointer input, so the click lands on the widget around it.
        await element.ClickAsync(new() { Force = true });

        var actionBar = GetWidgetActionBar(element.Page);
        await Assertions.Expect(actionBar).ToBeVisibleAsync(new() { Timeout = 30000 });
        return actionBar;
    }

    /// <summary>
    /// The bar with the actions of the selected widget. A page has at most one.
    /// </summary>
    public static ILocator GetWidgetActionBar(this IPage page) => page.GetByTestId("widget-action-bar");
}
