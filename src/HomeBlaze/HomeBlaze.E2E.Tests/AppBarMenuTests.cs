using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the menu behind the button at the right end of the app bar: the pages with <c>alignment: Menu</c>
/// above the developer mode, the time zone and the dark mode.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class AppBarMenuTests
{
    private const int Timeout = 30000;
    private const string MenuPageUrl = "pages/MenuPage.md";

    private readonly PlaywrightFixture _fixture;

    public AppBarMenuTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAPageIsInTheMenu_ThenTheAppBarHasNoIconForItNorForTheEntriesOfTheMenu()
    {
        // Arrange & Act
        var page = await OpenAsync("pages/Dashboard.md");

        // Assert
        var appBar = page.GetByRole(AriaRole.Banner);
        await Assertions.Expect(appBar.Locator($"a[href='{MenuPageUrl}']")).ToHaveCountAsync(0);
        await Assertions.Expect(appBar.Locator("a[href='pages/Dashboard.md']")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(appBar.GetByTitle("Toggle Dark Mode")).ToHaveCountAsync(0);
        await Assertions.Expect(appBar.GetByTitle("Toggle Developer Mode")).ToHaveCountAsync(0);
        await Assertions.Expect(page.AppBarMenu().GetByRole(AriaRole.Button, new() { Name = "Open menu", Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTheMenuIsOpened_ThenTheMenuPageIsAnEntryAboveTheBuiltInEntries()
    {
        // Arrange
        var page = await OpenAsync("pages/Dashboard.md");

        // Act
        await page.OpenAppBarMenuAsync();

        // Assert
        var entries = page.Locator(".mud-popover-open .app-bar-menu-list > .mud-menu-item, .mud-popover-open .app-bar-menu-list > .mud-menu");
        await Assertions.Expect(entries.First).ToHaveTextAsync("Menu Page", new() { Timeout = Timeout });
        await Assertions.Expect(entries.First).ToHaveAttributeAsync("href", MenuPageUrl);
        await Assertions.Expect(entries.First.Locator(".mud-menu-item-icon")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator($".mud-popover-open .app-bar-menu-list > a[href='{MenuPageUrl}'] + .mud-divider")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("app-bar-menu-developer-mode")).ToBeVisibleAsync();
        await Assertions.Expect(page.TimeZoneMenuEntry()).ToContainTextAsync("Automatic (");
        await Assertions.Expect(page.DarkModeMenuEntry()).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WhenTheMenuPageIsClicked_ThenItOpensAndItsEntryIsMarkedAsTheOpenPage()
    {
        // Arrange
        var page = await OpenAsync("pages/Dashboard.md");
        await page.OpenAppBarMenuAsync();

        // Act
        await page.Locator($".mud-popover-open a[href='{MenuPageUrl}']").ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith(MenuPageUrl, StringComparison.Ordinal), new() { Timeout = Timeout });

        // Assert
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Menu Page", Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = Timeout });
        await page.OpenAppBarMenuAsync();
        await Assertions.Expect(page.Locator($".mud-popover-open a[href='{MenuPageUrl}']"))
            .ToHaveAttributeAsync("aria-current", "page", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTheDarkModeEntryIsClicked_ThenTheLightModeIsOnAndTheMenuStaysOpen()
    {
        // Arrange: the app starts in the dark theme.
        var page = await OpenAsync("pages/Dashboard.md");
        await Assertions.Expect(page.Locator(".homeblaze-dark-mode")).ToHaveCountAsync(1);
        await page.OpenAppBarMenuAsync();
        await Assertions.Expect(page.DarkModeMenuEntry()).ToHaveAttributeAsync("aria-checked", "true");

        // Act
        await page.DarkModeMenuEntry().ClickAsync();

        // Assert
        await Assertions.Expect(page.Locator(".homeblaze-dark-mode")).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(page.DarkModeMenuEntry()).ToHaveAttributeAsync("aria-checked", "false");
        await Assertions.Expect(page.DarkModeMenuEntry()).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WhenTheDeveloperModeEntryIsClicked_ThenItShowsTheNewState()
    {
        // Arrange
        var page = await OpenAsync("pages/Dashboard.md");
        await page.OpenAppBarMenuAsync();
        var entry = page.GetByTestId("app-bar-menu-developer-mode");
        var wasEnabled = await entry.GetAttributeAsync("aria-checked") == "true";

        try
        {
            // Act
            await entry.ClickAsync();

            // Assert
            await Assertions.Expect(entry).ToHaveAttributeAsync("aria-checked", wasEnabled ? "false" : "true", new() { Timeout = Timeout });
        }
        finally
        {
            // The developer mode is one for the whole server, so the other tests get it back as it was.
            await entry.ClickAsync();
            await Assertions.Expect(entry).ToHaveAttributeAsync("aria-checked", wasEnabled ? "true" : "false", new() { Timeout = Timeout });
        }
    }

    private async Task<IPage> OpenAsync(string path)
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}{path}");
        await page.WaitUntilInteractiveAsync(Timeout);
        return page;
    }
}
