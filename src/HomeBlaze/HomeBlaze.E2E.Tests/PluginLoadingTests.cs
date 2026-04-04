using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests that verify NuGet plugins are loaded and displayed correctly in the browser.
/// Requires the sample plugin nupkg files to be available in the Plugins folder.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class PluginLoadingTests
{
    private const int PageLoadTimeout = 30000;
    private const int ElementVisibilityTimeout = 10000;
    private const int BlazorRenderDelay = 500;

    private readonly PlaywrightFixture _fixture;

    public PluginLoadingTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task BothSamplePlugins_ShouldAppearInPluginManager()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act - Navigate to browser and open the Plugins subject
        await NavigateToPluginsAsync(page);

        // Assert - Both plugin entries should be visible in the LoadedPlugins section
        var plugin1 = page.GetByText("MyCompany.SamplePlugin1.HomeBlaze v1.0.0");
        await Assertions.Expect(plugin1).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        var plugin2 = page.GetByText("MyCompany.SamplePlugin2.HomeBlaze v1.0.0");
        await Assertions.Expect(plugin2).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task PluginDetail_ShouldShowHostDependencies()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act - Navigate to browser, open Plugins, then click on SamplePlugin1
        await NavigateToPluginsAsync(page);

        var plugin1 = page.GetByText("MyCompany.SamplePlugin1.HomeBlaze v1.0.0");
        await Assertions.Expect(plugin1).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await plugin1.ClickAsync();
        await page.WaitForTimeoutAsync(BlazorRenderDelay);

        // Assert - The plugin detail pane should show HostDependencies containing MyCompany.Abstractions
        var hostDependencies = page.GetByText("MyCompany.Abstractions");
        await Assertions.Expect(hostDependencies).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    private async Task NavigateToPluginsAsync(IPage page)
    {
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Navigate to the Browser page
        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await Assertions.Expect(browserLink).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await browserLink.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/browser"), new() { Timeout = PageLoadTimeout });

        // Click on the Plugins entry in the root subject tree
        var pluginsEntry = page.GetByText("Plugins").First;
        await Assertions.Expect(pluginsEntry).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await pluginsEntry.ClickAsync();
        await page.WaitForTimeoutAsync(BlazorRenderDelay);
    }
}
