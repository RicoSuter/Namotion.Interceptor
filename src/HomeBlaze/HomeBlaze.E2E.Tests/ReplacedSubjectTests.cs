using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.E2E.Tests.TestSubjects;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for views that show a placeholder subject while storage replaces it with the real subject at the same path.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class ReplacedSubjectTests
{
    private const int PageLoadTimeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public ReplacedSubjectTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenTypeOfOpenPlaceholderIsLoaded_ThenBrowserPaneShowsRealSubject()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}browser/LateLoadedDevice");

        // The last pane is the selected subject; the root pane before it lists the real subject on its own.
        var selectedPaneTitle = page.Locator("#scrollContainer h1").Last;
        await Assertions.Expect(selectedPaneTitle).ToHaveTextAsync("LateLoadedDevice", new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(page.GetByText("Type is not loaded.")).ToBeVisibleAsync();

        // Act
        _fixture.ServerServices.GetRequiredService<TypeProvider>().AddTypes([typeof(LateLoadedDevice)]);

        // Assert
        await Assertions.Expect(selectedPaneTitle).ToHaveTextAsync("E2E Late Loaded Device", new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(page.GetByText("Type is not loaded.")).Not.ToBeVisibleAsync();
        Assert.EndsWith("/browser/LateLoadedDevice", page.Url);
    }

    [Fact]
    public async Task WhenPlaceholderTypeIsFixedAndSaved_ThenPageShowsRealSubject()
    {
        // Arrange - the test data is reset from the source before the server starts
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/MistypedMotor");
        await Assertions.Expect(page).ToHaveTitleAsync("MistypedMotor - HomeBlaze", new() { Timeout = PageLoadTimeout });

        var editMenu = page.Locator("[data-testid='edit-mode-menu']");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await editMenu.ClickAsync();
        await page.GetByText("Source").ClickAsync();

        // The JSON editor reads the file after it is created, so the content is set once that has happened.
        await page.WaitForFunctionAsync(
            "() => window.monaco?.editor?.getEditors().length > 0",
            null,
            new() { Timeout = PageLoadTimeout });
        await page.EvaluateAsync(
            "content => monaco.editor.getEditors()[0].setValue(content)",
            """
            {
              "$type": "HomeBlaze.Samples.Motor",
              "name": "E2E Saved Motor"
            }
            """);
        await Assertions.Expect(page.Locator(".monaco-editor .view-lines")).ToContainTextAsync("HomeBlaze.Samples.Motor\"");

        // Act
        await page.Locator("[data-testid='save-button'] button").ClickAsync();

        // Assert
        await Assertions.Expect(page).ToHaveTitleAsync("E2E Saved Motor - HomeBlaze", new() { Timeout = PageLoadTimeout });
    }
}
