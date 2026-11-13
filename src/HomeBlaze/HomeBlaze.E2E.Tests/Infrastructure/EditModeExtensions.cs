using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Selects the edit modes of a page through the page menu of its head.
/// </summary>
internal static class EditModeExtensions
{
    /// <summary>
    /// Opens the page menu and selects a mode of it by its exact name, such as View, Split, Source or Inline. The mode is
    /// found among the items of the menu, so that the title of the page, which the menu shows too, is never taken for it.
    /// The page must be interactive, because a click on the prerendered menu is lost.
    /// </summary>
    public static async Task SelectEditModeAsync(this IPage page, string mode, float timeout = 30000)
    {
        var editMenu = page.GetByTestId("edit-mode-menu");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = timeout });
        await editMenu.ClickAsync();
        await page.Locator(".mud-popover-open").GetByRole(AriaRole.Menuitem, new() { Name = mode, Exact = true }).ClickAsync();
    }
}
