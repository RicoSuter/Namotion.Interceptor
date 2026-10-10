using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for navigation functionality.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
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

        // Assert - MudBlazor's MudAppBar renders as <header> (ARIA role "banner")
        var appBar = page.GetByRole(AriaRole.Banner);
        await Assertions.Expect(appBar).ToBeVisibleAsync(new() { Timeout = 30000 });
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

    [Fact]
    public async Task WhenPageIsAddedToExpandedFolder_ThenNavMenuShowsIt()
    {
        // Arrange
        var services = _fixture.ServerServices;
        var root = (FluentStorageContainer)services.GetRequiredService<RootManager>().Root!;
        var folder = (VirtualFolder)root.Children["Demo"];
        var originalChildren = folder.Children;

        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        var navigation = page.Locator(".mud-navmenu");
        var folderHeader = navigation.GetByRole(AriaRole.Button, new() { Name = "Demo" });
        await Assertions.Expect(folderHeader).ToBeVisibleAsync(new() { Timeout = 30000 });
        await folderHeader.ClickAsync();
        await Assertions.Expect(navigation.GetByRole(AriaRole.Link, new() { Name = "Grid Demo" }))
            .ToBeVisibleAsync(new() { Timeout = 30000 });

        try
        {
            // Act
            var addedPage = (MarkdownFile)ActivatorUtilities.CreateInstance(
                services, typeof(MarkdownFile), root, "Demo/AddedLater.md");

            folder.Children = new Dictionary<string, IInterceptorSubject>(originalChildren)
            {
                ["AddedLater.md"] = addedPage
            };

            // Assert
            await Assertions.Expect(navigation.GetByRole(AriaRole.Link, new() { Name = "AddedLater" }))
                .ToBeVisibleAsync(new() { Timeout = 30000 });
        }
        finally
        {
            folder.Children = originalChildren;
        }
    }
}
