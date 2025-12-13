using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for navigation functionality.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
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

        // Wait for Blazor to initialize and redirect
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

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
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Assert - AppBar should have navigation links
        var toolbar = page.GetByRole(AriaRole.Toolbar);
        await Assertions.Expect(toolbar).ToBeVisibleAsync();
    }

    [Fact]
    public async Task NavMenu_ShouldContainBrowserLink()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Assert - Nav menu should have Browser link
        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await Assertions.Expect(browserLink).ToBeVisibleAsync();
    }

    [Fact]
    public async Task BrowserLink_ShouldNavigateToBrowserPage()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Act
        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await browserLink.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Assert
        Assert.Contains("/browser", page.Url);
    }
}
