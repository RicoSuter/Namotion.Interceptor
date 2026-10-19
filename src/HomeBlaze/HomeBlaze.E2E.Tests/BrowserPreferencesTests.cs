using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
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

    // Literal, so a rename that loses what browsers stored fails here.
    private const string DrawerCookie = "homeblaze.drawer";
    private const string RowLayoutCookie = "homeblaze.rowlayout";
    private const string TimeZoneCookie = "homeblaze.timezone";

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
            new Cookie { Name = TimeZoneCookie, Value = "Europe/Berlin", Url = _fixture.ServerAddress }
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
        await Assertions.Expect(Drawer(page)).ToHaveClassAsync(OpenDrawer, new() { Timeout = PageLoadTimeout });
        await ToggleDrawerAsync(page);
        await Assertions.Expect(Drawer(page)).ToHaveClassAsync(ClosedDrawer, new() { Timeout = PageLoadTimeout });
        await WaitForDrawerCookieAsync(page, "closed");

        // Act
        await page.ReloadAsync();
        await page.WaitUntilInteractiveAsync();

        // Assert
        await Assertions.Expect(Drawer(page)).ToHaveClassAsync(ClosedDrawer, new() { Timeout = PageLoadTimeout });
    }

    [Fact]
    public async Task WhenTheDrawerIsOpenedAgainAndThePageIsReloaded_ThenItStaysOpen()
    {
        // Arrange
        var page = await OpenAsync();
        await ToggleDrawerAsync(page);
        await WaitForDrawerCookieAsync(page, "closed");
        await ToggleDrawerAsync(page);
        await Assertions.Expect(Drawer(page)).ToHaveClassAsync(OpenDrawer, new() { Timeout = PageLoadTimeout });
        await WaitForDrawerCookieAsync(page, "open");

        // Act
        await page.ReloadAsync();
        await page.WaitUntilInteractiveAsync();

        // Assert
        await Assertions.Expect(Drawer(page)).ToHaveClassAsync(OpenDrawer, new() { Timeout = PageLoadTimeout });

        // The attributes the browser keeps it under: for every page, and for about a year.
        var cookie = Assert.Single(await page.Context.CookiesAsync(), cookie => cookie.Name == DrawerCookie);
        Assert.Equal("/", cookie.Path);
        Assert.True(cookie.Expires > DateTimeOffset.UtcNow.AddDays(300).ToUnixTimeSeconds());
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

    [Fact]
    public async Task WhenOnlyThePrerenderReceivesTheDrawerCookie_ThenTheInteractivePageKeepsTheDrawerClosed()
    {
        // Arrange
        // Scoped to the page, so the request that connects the circuit (/_blazor) does not carry it: the circuit can only
        // know the value from what the prerender hands over.
        var page = await _fixture.CreatePageAsync();
        await page.Context.AddCookiesAsync(
        [
            new Cookie { Name = DrawerCookie, Value = "closed", Domain = new Uri(_fixture.ServerAddress).Host, Path = "/browser" }
        ]);

        // Act
        await page.GotoAsync($"{_fixture.ServerAddress}{BrowserPath}");
        await page.WaitUntilInteractiveAsync();

        // Assert
        await Assertions.Expect(Drawer(page)).ToHaveClassAsync(ClosedDrawer, new() { Timeout = PageLoadTimeout });
    }

    [Fact]
    public async Task WhenTheDrawerClosesItselfOnANarrowView_ThenItsStateIsNotStored()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });

        // Act
        await page.GotoAsync($"{_fixture.ServerAddress}{BrowserPath}");
        await page.WaitForFunctionAsync(SubjectBrowserPage.IsInteractive, null, new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(Drawer(page)).ToHaveClassAsync(ClosedDrawer, new() { Timeout = PageLoadTimeout });

        // A later write through the same store, which reaches the browser after a write of the closed drawer would have.
        await page.Locator("[data-testid='row-layout-menu']").GetByRole(AriaRole.Button).ClickAsync();
        await page.Locator("[data-testid='row-layout-stacked']").ClickAsync();
        await WaitForCookieAsync(page, $"{RowLayoutCookie}=Stacked");

        // Assert
        var cookies = await page.Context.CookiesAsync();
        Assert.DoesNotContain(cookies, cookie => cookie.Name == DrawerCookie);
    }

    [Fact]
    public async Task WhenATimeZoneIsPicked_ThenItIsKeptAcrossAReload()
    {
        // Arrange
        var page = await OpenAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Select timezone", Exact = true }).ClickAsync();
        await page.GetByPlaceholder("Search timezone").FillAsync("Berlin");
        await page.Locator(".mud-popover-open .mud-list-item")
            .Filter(new() { HasTextRegex = new Regex(@"^Europe/Berlin \(") })
            .ClickAsync();

        // The value as JavaScript encodes it.
        await WaitForCookieAsync(page, $"{TimeZoneCookie}=Europe%2FBerlin");

        // Act
        await page.ReloadAsync();
        await page.WaitForFunctionAsync(SubjectBrowserPage.IsInteractive, null, new() { Timeout = PageLoadTimeout });

        // Assert
        await Assertions.Expect(page.GetByTitle("Europe/Berlin", new() { Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
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
        page.GetByTitle("Toggle Navigation", new() { Exact = true }).ClickAsync();

    private static Task WaitForDrawerCookieAsync(IPage page, string value) =>
        WaitForCookieAsync(page, $"{DrawerCookie}={value}");

    private static Task WaitForCookieAsync(IPage page, string cookie) =>
        page.WaitForFunctionAsync(
            "cookie => document.cookie.split('; ').includes(cookie)",
            cookie,
            new() { Timeout = PageLoadTimeout });
}
