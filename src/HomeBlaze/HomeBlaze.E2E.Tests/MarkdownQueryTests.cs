using HomeBlaze.E2E.Tests.Infrastructure;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the queries and operations of a Markdown page in the subject browser.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class MarkdownQueryTests
{
    private const int PageLoadTimeout = 30000;
    private const int ElementVisibilityTimeout = 15000;

    private readonly PlaywrightFixture _fixture;

    public MarkdownQueryTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenReadingTheEvaluatedContentWithSourcePaths_ThenTheDialogOffersTheOptionAndShowsTheContent()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}browser/Dashboard.md");
        await page.WaitForFunctionAsync(SubjectBrowserPage.IsInteractive, null, new() { Timeout = PageLoadTimeout });

        var queryButton = page.GetByRole(AriaRole.Button, new() { Name = "Read Evaluated Content" });
        await Assertions.Expect(queryButton).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

        // Act
        await queryButton.ClickAsync();
        var sourcePathsOption = page.GetByRole(AriaRole.Checkbox, new() { Name = "IncludeSourcePaths" });
        await Assertions.Expect(sourcePathsOption).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await sourcePathsOption.CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Execute" }).ClickAsync();

        // Assert
        var resultDialog = page.GetByRole(AriaRole.Dialog).Filter(new() { HasText = "completed successfully" });
        await Assertions.Expect(resultDialog.GetByText("Welcome to HomeBlaze E2E Test Dashboard.")).ToBeVisibleAsync(
            new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenOpeningReplaceText_ThenTheDialogAsksForTheOldAndTheNewText()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}browser/Dashboard.md");
        await page.WaitForFunctionAsync(SubjectBrowserPage.IsInteractive, null, new() { Timeout = PageLoadTimeout });

        var operationButton = page.GetByRole(AriaRole.Button, new() { Name = "Replace Text" });
        await Assertions.Expect(operationButton).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Replace Content" })).ToBeVisibleAsync();

        // Act
        await operationButton.ClickAsync();

        // Assert
        var dialog = page.GetByRole(AriaRole.Dialog).Filter(new() { HasText = "Replaces one exact, unique passage of the markdown source." });
        await Assertions.Expect(dialog.GetByLabel(new Regex("^OldText"))).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(dialog.GetByLabel(new Regex("^NewText"))).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Assertions.Expect(dialog).ToBeHiddenAsync(new() { Timeout = ElementVisibilityTimeout });
    }
}
