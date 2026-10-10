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
        var (services, root, folder) = GetDemoFolder();
        var originalChildren = folder.Children;
        var navigation = await OpenWithDemoFolderExpandedAsync();

        try
        {
            // Act
            folder.Children = new Dictionary<string, IInterceptorSubject>(originalChildren)
            {
                ["AddedLater.md"] = CreatePage(services, root, "Demo/AddedLater.md")
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

    [Fact]
    public async Task WhenPageIsRemovedFromExpandedFolder_ThenNavMenuDropsIt()
    {
        // Arrange
        var (services, root, folder) = GetDemoFolder();
        var originalChildren = folder.Children;

        try
        {
            folder.Children = new Dictionary<string, IInterceptorSubject>(originalChildren)
            {
                ["RemovedLater.md"] = CreatePage(services, root, "Demo/RemovedLater.md")
            };

            var navigation = await OpenWithDemoFolderExpandedAsync();
            var link = navigation.GetByRole(AriaRole.Link, new() { Name = "RemovedLater" });
            await Assertions.Expect(link).ToBeVisibleAsync(new() { Timeout = 30000 });

            // Act
            folder.Children = originalChildren;

            // Assert
            await Assertions.Expect(link).ToHaveCountAsync(0, new() { Timeout = 30000 });
        }
        finally
        {
            folder.Children = originalChildren;
        }
    }

    [Fact]
    public async Task WhenFolderAppearsBeforeExpandedFolder_ThenExpandedStateStaysWithItsFolder()
    {
        // Arrange
        var (services, root, _) = GetDemoFolder();
        var originalRootChildren = root.Children;
        var navigation = await OpenWithDemoFolderExpandedAsync();

        try
        {
            // Act
            root.Children = new Dictionary<string, IInterceptorSubject>(originalRootChildren)
            {
                ["Aaa"] = CreateFolderWithPage(services, root, "Aaa", "InsertedInner.md")
            };

            // Assert
            await Assertions.Expect(navigation.GetByRole(AriaRole.Button, new() { Name = "Aaa" }))
                .ToBeVisibleAsync(new() { Timeout = 30000 });
            await Assertions.Expect(navigation.GetByRole(AriaRole.Link, new() { Name = "Grid Demo" }))
                .ToBeVisibleAsync();
            await Assertions.Expect(navigation.GetByRole(AriaRole.Link, new() { Name = "InsertedInner" }))
                .ToHaveCountAsync(0);
        }
        finally
        {
            root.Children = originalRootChildren;
        }
    }

    private (IServiceProvider Services, FluentStorageContainer Root, VirtualFolder Folder) GetDemoFolder()
    {
        var services = _fixture.ServerServices;
        var root = (FluentStorageContainer)services.GetRequiredService<RootManager>().Root!;
        return (services, root, (VirtualFolder)root.Children["Demo"]);
    }

    private async Task<ILocator> OpenWithDemoFolderExpandedAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        var navigation = page.Locator(".mud-navmenu");
        var folderHeader = navigation.GetByRole(AriaRole.Button, new() { Name = "Demo" });
        await Assertions.Expect(folderHeader).ToBeVisibleAsync(new() { Timeout = 30000 });
        await folderHeader.ClickAsync();
        await Assertions.Expect(navigation.GetByRole(AriaRole.Link, new() { Name = "Grid Demo" }))
            .ToBeVisibleAsync(new() { Timeout = 30000 });

        return navigation;
    }

    private static MarkdownFile CreatePage(IServiceProvider services, FluentStorageContainer root, string path)
    {
        return (MarkdownFile)ActivatorUtilities.CreateInstance(services, typeof(MarkdownFile), root, path);
    }

    private static VirtualFolder CreateFolderWithPage(
        IServiceProvider services, FluentStorageContainer root, string folderPath, string pageName)
    {
        return new VirtualFolder(root, folderPath + "/")
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                [pageName] = CreatePage(services, root, $"{folderPath}/{pageName}")
            }
        };
    }
}
