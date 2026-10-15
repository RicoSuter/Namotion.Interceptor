using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services.Diagnostics;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class InstallationWidgetTests
{
    private const int Timeout = 30000;

    // A page of the test data that tests may overwrite.
    private const string ScratchPage = "Scratch.md";

    private readonly PlaywrightFixture _fixture;

    public InstallationWidgetTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenTheWidgetIsOnAPage_ThenItShowsVersionChannelAndVersionLink()
    {
        // Arrange
        var (name, installation) = await _fixture.AddSubjectToRootAsync<HomeBlazeInstallation>();
        try
        {
            await _fixture.WriteDemoPageAsync(ScratchPage,
                $"---\ntitle: Scratch\n---\n\n# Scratch\n\n:: HomeBlaze.Components.Widget Path=\"/{name}\"\n::\n");
            var page = await _fixture.CreatePageAsync();

            // Act
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ScratchPage}");

            // Assert
            var widget = page.Locator("[data-testid='installation-widget']");
            await Assertions.Expect(widget).ToContainTextAsync(installation.Version, new() { Timeout = Timeout });
            await Assertions.Expect(widget.Locator("[data-testid='installation-widget-channel']"))
                .ToHaveTextAsync(installation.Channel.ToString());
            await Assertions.Expect(widget.Locator("[data-testid='installation-widget-version-link']"))
                .ToHaveAttributeAsync("href", new Regex("/commit/[0-9a-f]{40}$"));
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, installation);
        }
    }
}
