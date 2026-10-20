using System.Text.Json;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for HomeBlaze as an installed web app: the manifest and the icons the browser installs it with, and the
/// menu entries that stand in for the address bar and the reload button it does not have.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class WebAppTests
{
    private const int Timeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public WebAppTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenThePageIsLoaded_ThenItsHeadLinksTheManifestAndTheTouchIcon()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Dashboard.md");

        // Assert
        await Assertions.Expect(page.Locator("head link[rel='manifest']")).ToHaveAttributeAsync("href", "manifest.webmanifest");
        await Assertions.Expect(page.Locator("head link[rel='apple-touch-icon']")).ToHaveAttributeAsync("href", "icons/apple-touch-icon.png");
        await Assertions.Expect(page.Locator("head meta[name='apple-mobile-web-app-capable']")).ToHaveAttributeAsync("content", "yes");
    }

    [Fact]
    public async Task WhenTheManifestIsRequested_ThenItOpensTheAppStandaloneWithIconsThatAreServed()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        var response = await page.APIRequest.GetAsync($"{_fixture.ServerAddress}manifest.webmanifest");

        // Assert
        Assert.Equal(200, response.Status);
        Assert.StartsWith("application/manifest+json", response.Headers["content-type"]);
        using var manifest = JsonDocument.Parse(await response.TextAsync());
        Assert.Equal("HomeBlaze", manifest.RootElement.GetProperty("name").GetString());
        Assert.Equal("standalone", manifest.RootElement.GetProperty("display").GetString());
        Assert.Equal("/", manifest.RootElement.GetProperty("start_url").GetString());

        var sources = manifest.RootElement.GetProperty("icons").EnumerateArray()
            .Select(icon => icon.GetProperty("src").GetString()!)
            .Append("icons/apple-touch-icon.png");
        foreach (var source in sources)
        {
            var icon = await page.APIRequest.GetAsync($"{_fixture.ServerAddress}{source}");
            Assert.Equal(200, icon.Status);
            Assert.Equal("image/png", icon.Headers["content-type"]);
        }
    }

    [Fact]
    public async Task WhenTheHomeEntryOfTheMenuIsClicked_ThenTheStartPageOpens()
    {
        // Arrange
        var page = await OpenAsync("pages/Demo/Spacing.md");
        await page.OpenAppBarMenuAsync();

        // Act
        await page.GetByTestId("app-bar-menu-home").ClickAsync();

        // Assert: the start page redirects to the first page of the app bar.
        await page.WaitForURLAsync(url => url.EndsWith("pages/Dashboard.md", StringComparison.Ordinal), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTheReloadEntryOfTheMenuIsClicked_ThenTheOpenPageIsLoadedAgain()
    {
        // Arrange
        var page = await OpenAsync("pages/Demo/Spacing.md");
        await page.EvaluateAsync("() => window.notReloaded = true");
        await page.OpenAppBarMenuAsync();

        // Act
        await page.GetByTestId("app-bar-menu-reload").ClickAsync();

        // Assert: a new document, which has none of the state of the old one, at the same address.
        await page.WaitForFunctionAsync("() => window.notReloaded === undefined", null, new() { Timeout = Timeout });
        Assert.EndsWith("pages/Demo/Spacing.md", page.Url, StringComparison.Ordinal);
        await page.WaitUntilInteractiveAsync(Timeout);
    }

    private async Task<IPage> OpenAsync(string path)
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}{path}");
        await page.WaitUntilInteractiveAsync(Timeout);
        return page;
    }
}
