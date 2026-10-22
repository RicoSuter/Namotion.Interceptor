using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the editing of the text of a markdown page in inline edit mode: its blocks of text are selected, edited in
/// a dialog, deleted and inserted.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class InlineTextEditingTests
{
    private const int Timeout = 30000;
    private const string PageName = "TextEditing.md";

    // The server writes the page in its output folder, so a run that ends with a change of the page would change the
    // baseline of the next run if it were read from there.
    private const string Content =
        "---\n"
        + "title: Text Editing Demo\n"
        + "---\n"
        + "\n"
        + "# Text Editing Demo\n"
        + "\n"
        + "First paragraph on the page.\n"
        + "\n"
        + ":: HomeBlaze.Components.Card Title=\"Text Card\"\n"
        + "Card body text.\n"
        + "::\n"
        + "\n"
        + "Last paragraph on the page.\n";

    private readonly PlaywrightFixture _fixture;

    public InlineTextEditingTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAParagraphOnThePageIsEdited_ThenTheFileHasTheNewTextAndItStaysSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "First paragraph");

        // Act
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        var input = page.GetByTestId("markdown-text-input");
        await Assertions.Expect(input).ToHaveValueAsync("First paragraph on the page.", new() { Timeout = Timeout });
        await input.FillAsync("Changed *paragraph*.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("# Text Editing Demo\n\nChanged *paragraph*.\n\n:: HomeBlaze.Components.Card"),
            "The paragraph was not changed.");
        await Assertions.Expect(GetTextWrapper(page, "Changed paragraph")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAParagraphInACardBodyIsEdited_ThenOnlyTheCardBodyChanges()
    {
        // Arrange: the first click selects the card, and the text of its body is selectable once the card is selected.
        var page = await OpenPageInInlineModeAsync();
        await page.GetCard("Text Card").Locator(".mud-card-header").SelectWidgetAsync();
        var actionBar = await SelectTextAsync(page, "Card body text");

        // Act
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await page.GetByTestId("markdown-text-input").FillAsync("Changed body.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains(":: HomeBlaze.Components.Card Title=\"Text Card\"\nChanged body.\n::\n") &&
                    text.Contains("\nFirst paragraph on the page.\n"),
            "The card body was not changed.");
    }

    [Fact]
    public async Task WhenATextIsDeleted_ThenItIsRemovedFromTheFile()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "Last paragraph");

        // Act: the bar has a button named Delete as well, so the one of the dialog is looked up in the dialog.
        await actionBar.GetByTestId("delete-block-button").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => !text.Contains("Last paragraph") && text.EndsWith("::\n", StringComparison.Ordinal), "The text was not deleted.");
    }

    [Fact]
    public async Task WhenTheTextChangedWhileItsDialogWasOpen_ThenTheSaveIsRefusedAndTheDialogKeepsTheInput()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "Last paragraph");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        var input = page.GetByTestId("markdown-text-input");
        await Assertions.Expect(input).ToHaveValueAsync("Last paragraph on the page.", new() { Timeout = Timeout });
        await _fixture.WriteDemoPageAsync(PageName, Content.Replace("Last paragraph on the page.", "Changed elsewhere."));

        // Act
        await input.FillAsync("Mine.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("markdown-text-refusal")).ToContainTextAsync("changed", new() { Timeout = Timeout });
        await Assertions.Expect(input).ToHaveValueAsync("Mine.");
        var text = (await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(PageName))).Replace("\r\n", "\n");
        Assert.Contains("Changed elsewhere.", text);
        Assert.DoesNotContain("Mine.", text);
    }

    [Fact]
    public async Task WhenThePageIsViewed_ThenItsTextIsNotWrapped()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();

        // Act
        await page.SelectEditModeAsync("View", Timeout);

        // Assert
        await Assertions.Expect(page.Locator(".markdown-text")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    private async Task<IPage> OpenPageInInlineModeAsync()
    {
        await _fixture.WriteDemoPageAsync(PageName, Content);
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync(Timeout);
        await page.SwitchToInlineModeAsync();
        return page;
    }

    // Clicks the start of the text: the insert lines of a selected container center their buttons on the edges of its
    // content, where they cover the middle of a single line of text. The bar of a widget that was selected before shows
    // until the selection moves, so the wait for the selected text keeps a click on the bar from reaching that widget.
    private static async Task<ILocator> SelectTextAsync(IPage page, string text)
    {
        var wrapper = GetTextWrapper(page, text);
        var actionBar = await wrapper.SelectWidgetAsync(new Position { X = 8, Y = 8 });
        await Assertions.Expect(wrapper).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        return actionBar;
    }

    private static ILocator GetTextWrapper(IPage page, string text) =>
        page.Locator(".subject-component-wrapper.selectable:has(> .markdown-text)").Filter(new() { HasText = text });
}
