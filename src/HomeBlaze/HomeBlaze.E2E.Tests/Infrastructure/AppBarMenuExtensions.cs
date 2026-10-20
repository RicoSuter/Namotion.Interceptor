using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Drives the menu behind the button at the right end of the app bar: the menu pages, the developer mode, the time
/// zone and the dark mode.
/// </summary>
internal static class AppBarMenuExtensions
{
    private const int Timeout = 30000;

    /// <summary>The menu, with the button that opens it.</summary>
    public static ILocator AppBarMenu(this IPage page) => page.GetByTestId("app-bar-menu");

    /// <summary>The entry of the dark mode, rendered while the menu is open.</summary>
    public static ILocator DarkModeMenuEntry(this IPage page) => page.GetByTestId("app-bar-menu-dark-mode");

    /// <summary>The entry of the time zone, rendered while the menu is open.</summary>
    public static ILocator TimeZoneMenuEntry(this IPage page) => page.GetByTestId("time-zone-menu");

    /// <summary>Opens the menu and waits until its entries are shown.</summary>
    public static async Task OpenAppBarMenuAsync(this IPage page)
    {
        await page.AppBarMenu().GetByRole(AriaRole.Button, new() { Name = "Open menu", Exact = true }).ClickAsync();
        await Assertions.Expect(page.DarkModeMenuEntry()).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    /// <summary>Closes the open menu and waits until its entries are gone.</summary>
    public static async Task CloseAppBarMenuAsync(this IPage page)
    {
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.DarkModeMenuEntry()).ToBeHiddenAsync(new() { Timeout = Timeout });
    }

    /// <summary>Switches between the dark and the light mode through the menu and closes it again.</summary>
    public static async Task ToggleDarkModeAsync(this IPage page)
    {
        await page.OpenAppBarMenuAsync();
        var isDarkMode = await page.DarkModeMenuEntry().GetAttributeAsync("aria-checked") == "true";
        await page.DarkModeMenuEntry().ClickAsync();
        await Assertions.Expect(page.DarkModeMenuEntry())
            .ToHaveAttributeAsync("aria-checked", isDarkMode ? "false" : "true", new() { Timeout = Timeout });
        await page.CloseAppBarMenuAsync();
    }

    /// <summary>Opens the menu and the submenu of the time zone, which lists the zones below a search field.</summary>
    public static async Task OpenTimeZoneMenuAsync(this IPage page)
    {
        await page.OpenAppBarMenuAsync();
        await page.TimeZoneMenuEntry().ClickAsync();
        await Assertions.Expect(page.GetByPlaceholder("Search timezone")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }
}
