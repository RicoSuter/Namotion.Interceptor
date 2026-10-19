using HomeBlaze.E2E.Tests.Infrastructure;
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
}
