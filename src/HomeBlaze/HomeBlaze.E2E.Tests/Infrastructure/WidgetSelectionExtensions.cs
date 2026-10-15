using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Selects the widgets of a page in edit mode, which shows the bar with the actions of the selected widget.
/// </summary>
internal static class WidgetSelectionExtensions
{
    private const int Timeout = 30000;

    /// <summary>
    /// Switches the page to inline edit mode, in which a click selects a widget.
    /// </summary>
    public static async Task SwitchToInlineModeAsync(this IPage page)
    {
        var editMenu = page.GetByTestId("edit-mode-menu");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = Timeout });
        await editMenu.ClickAsync();
        await page.Locator(".mud-popover-open").GetByText("Inline", new() { Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selectable").First).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    /// <summary>
    /// Clicks the middle of the element, which selects the innermost widget there, and returns the bar with the
    /// actions of that widget.
    /// </summary>
    public static async Task<ILocator> SelectWidgetAsync(this ILocator element)
    {
        // A click before the page has rendered in edit mode would not select anything.
        await Assertions.Expect(element.Page.Locator(".subject-component-wrapper.selectable").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(element).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Forced, because in edit mode the content of a widget takes no pointer input, so the click lands on the widget around it.
        await element.ClickAsync(new() { Force = true });

        var actionBar = GetWidgetActionBar(element.Page);
        await Assertions.Expect(actionBar).ToBeVisibleAsync(new() { Timeout = Timeout });
        return actionBar;
    }

    /// <summary>
    /// The bar with the actions of the selected widget. A page has at most one.
    /// </summary>
    public static ILocator GetWidgetActionBar(this IPage page) => page.GetByTestId("widget-action-bar");
}
