using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Host.Components.Layout;
using HomeBlaze.Host.Components.TimeZones;
using HomeBlaze.Host.Preferences;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the preferences a browser remembers across reloads.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class BrowserPreferencesTests
{
    private const int PageLoadTimeout = 30000;
    private const string BrowserPath = "browser/Demo/Motor";

    private static readonly Regex OpenDrawer = new(@"\bmud-drawer--open\b");
    private static readonly Regex ClosedDrawer = new(@"\bmud-drawer--closed\b");

    // The opening tag of the drawer element, with both its id and the closed class, in any attribute order.
    private static readonly Regex PrerenderedClosedDrawer =
        new("<[a-z]+(?=[^>]*\\bid=\"nav-drawer\")(?=[^>]*\\bmud-drawer--closed\\b)[^>]*>");

    private readonly PlaywrightFixture _fixture;

    public BrowserPreferencesTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenATimeZoneIsPinnedInTheCookie_ThenTheInteractivePageShowsIt()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.Context.AddCookiesAsync(
        [
            new Cookie { Name = BrowserPreferences.GetCookieName(TimeZoneInitializer.PreferenceKey), Value = "Europe/Berlin", Url = _fixture.ServerAddress }
        ]);

        // Act
        await page.GotoAsync($"{_fixture.ServerAddress}{BrowserPath}");
        await page.WaitForFunctionAsync(SubjectBrowserPage.IsInteractive, null, new() { Timeout = PageLoadTimeout });

        // Assert
        await Assertions.Expect(page.GetByTitle("Europe/Berlin", new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
    }

    [Fact]
    public async Task WhenTheDrawerIsClosedAndThePageIsReloaded_ThenItStaysClosed()
    {
        // Arrange
        var page = await OpenAsync();
        await Assertions.Expect(Drawer(page)).ToHaveClassAsync(OpenDrawer);
        await ToggleDrawerAsync(page);
        await Assertions.Expect(Drawer(page)).ToHaveClassAsync(ClosedDrawer);
        await WaitForDrawerCookieAsync(page, "closed");

        // Act
        await page.ReloadAsync();
        await page.WaitUntilInteractiveAsync();

        // Assert
        await Assertions.Expect(Drawer(page)).ToHaveClassAsync(ClosedDrawer);
    }

    [Fact]
    public async Task WhenTheDrawerIsOpenedAgainAndThePageIsReloaded_ThenItStaysOpen()
    {
        // Arrange
        var page = await OpenAsync();
        await ToggleDrawerAsync(page);
        await WaitForDrawerCookieAsync(page, "closed");
        await ToggleDrawerAsync(page);
        await Assertions.Expect(Drawer(page)).ToHaveClassAsync(OpenDrawer);
        await WaitForDrawerCookieAsync(page, "open");

        // Act
        await page.ReloadAsync();
        await page.WaitUntilInteractiveAsync();

        // Assert
        await Assertions.Expect(Drawer(page)).ToHaveClassAsync(OpenDrawer);
    }

    [Fact]
    public async Task WhenTheDrawerIsClosed_ThenThePrerenderedPageHasItClosed()
    {
        // Arrange
        var page = await OpenAsync();
        await ToggleDrawerAsync(page);
        await WaitForDrawerCookieAsync(page, "closed");

        // Act
        // The request context of the browser context sends its cookies but runs no script, so this is the prerendered HTML.
        var response = await page.Context.APIRequest.GetAsync(page.Url);
        var html = await response.TextAsync();

        // Assert
        Assert.Matches(PrerenderedClosedDrawer, html);
    }

    private async Task<IPage> OpenAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}{BrowserPath}");
        await page.WaitUntilInteractiveAsync();
        return page;
    }

    private static ILocator Drawer(IPage page) => page.Locator("#nav-drawer");

    private static Task ToggleDrawerAsync(IPage page) =>
        page.GetByTitle("Toggle navigation", new() { Exact = true }).ClickAsync();

    private static Task WaitForDrawerCookieAsync(IPage page, string value) =>
        page.WaitForFunctionAsync(
            "cookie => document.cookie.split('; ').includes(cookie)",
            $"{BrowserPreferences.GetCookieName(MainLayout.DrawerPreferenceKey)}={value}",
            new() { Timeout = PageLoadTimeout });
}
