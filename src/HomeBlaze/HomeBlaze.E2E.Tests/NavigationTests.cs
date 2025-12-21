using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for navigation functionality.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "E2E")]
public class NavigationTests
{
    private readonly PlaywrightFixture _fixture;

    public NavigationTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HomePage_ShouldRedirectToDefaultPage()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Wait for redirect to happen
        await page.WaitForURLAsync(url => url.Contains("/pages/"), new() { Timeout = 30000 });

        // Assert - should redirect to a page (Dashboard is the default)
        Assert.Contains("/pages/", page.Url);
    }

    [Fact]
    public async Task AppBar_ShouldContainNavigationLinks()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert - AppBar should have navigation links
        var toolbar = page.GetByRole(AriaRole.Toolbar);
        await Assertions.Expect(toolbar).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task NavMenu_ShouldContainBrowserLink()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert - Nav menu should have Browser link
        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await Assertions.Expect(browserLink).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task BrowserLink_ShouldNavigateToBrowserPage()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Act - wait for browser link to be visible before clicking
        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await Assertions.Expect(browserLink).ToBeVisibleAsync(new() { Timeout = 30000 });
        await browserLink.ClickAsync();

        // Wait for navigation to complete
        await page.WaitForURLAsync(url => url.Contains("/browser"), new() { Timeout = 30000 });

        // Assert
        Assert.Contains("/browser", page.Url);
    }
}
