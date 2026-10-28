using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for unsaved text of a page, which a navigation or a change of the edit mode throws away only once the user
/// confirms it.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class UnsavedTextTests
{
    private const int Timeout = 30000;
    private const string PageName = "TextEditing.md";
    private const string Content = "# Unsaved Text Demo\n\nSaved paragraph.\n";
    private const string Typed = "Typed but not saved.";

    // A beforeunload that is canceled is the prompt of the browser, which Blazor gives while the page has unsaved text.
    private const string AsksBeforeUnload =
        "() => { const event = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(event); return event.defaultPrevented; }";

    private readonly PlaywrightFixture _fixture;

    public UnsavedTextTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenALinkIsClickedWithUnsavedTextInTheSourceEditor_ThenKeepEditingKeepsTheTextAndDiscardNavigates()
    {
        // Arrange
        var page = await OpenPageAsync("Source");
        await TypeIntoPageEditorAsync(page);
        var link = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });

        // Act
        await link.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Keep editing", Exact = true }).ClickAsync();

        // As a user does, the link is clicked again only once the question is gone.
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
        var editorTextAfterKeep = await page.EvaluateAsync<string>("() => monaco.editor.getEditors()[0].getValue()");
        var urlAfterKeep = page.Url;
        await link.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true }).ClickAsync();

        // Assert
        Assert.Contains(Typed, editorTextAfterKeep);
        Assert.EndsWith($"pages/Demo/{PageName}", urlAfterKeep);
        await _fixture.WaitForUrlAsync(page, "**/browser", Timeout);
        Assert.DoesNotContain(Typed, await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(PageName)));
    }

    [Fact]
    public async Task WhenTheTextOfTheSourceEditorWasSavedAndALinkIsClicked_ThenThePageIsLeftWithoutAQuestion()
    {
        // Arrange
        var page = await OpenPageAsync("Source");
        await TypeIntoPageEditorAsync(page);
        await page.Locator("[data-testid='save-button'] button").ClickAsync();
        await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await page.WaitForFunctionAsync($"() => !({AsksBeforeUnload})()", null, new() { Timeout = Timeout });

        // Act
        await page.GetByRole(AriaRole.Link, new() { Name = "Browser" }).ClickAsync();

        // Assert
        await _fixture.WaitForUrlAsync(page, "**/browser", Timeout);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Keep editing", Exact = true })).ToHaveCountAsync(0);
        Assert.Contains(Typed, await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(PageName)));
    }

    [Fact]
    public async Task WhenAnotherModeIsChosenWithUnsavedTextInTheSplitEditor_ThenTheModeChangesOnlyOnceTheDiscardIsConfirmed()
    {
        // Arrange
        var page = await OpenPageAsync("Split");
        await TypeIntoPageEditorAsync(page);

        // Act
        await page.SelectEditModeAsync("Inline");
        await page.GetByRole(AriaRole.Button, new() { Name = "Keep editing", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".monaco-editor .view-lines")).ToContainTextAsync(Typed, new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("edit-splitter")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await page.SelectEditModeAsync("Inline");
        await page.GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("edit-splitter")).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selectable").First).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenALinkIsClickedWithChangedTextInTheTextDialog_ThenKeepEditingKeepsTheDialogAndDiscardNavigates()
    {
        // Arrange
        var page = await OpenPageAsync("Inline");
        var wrapper = page.GetTextWrapper("Saved paragraph.");
        var actionBar = await wrapper.SelectWidgetAsync();
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await page.WaitForTextEditorFocusAsync();
        await page.Keyboard.TypeAsync(Typed);
        await page.WaitForFunctionAsync(AsksBeforeUnload, null, new() { Timeout = Timeout });

        // Act: the dialog covers the links, so the app navigates as the back button of a phone would.
        await page.EvaluateAsync("url => Blazor.navigateTo(url)", "/browser");
        await page.GetByRole(AriaRole.Button, new() { Name = "Keep editing", Exact = true }).ClickAsync();
        var editorTextAfterKeep = await page.EvaluateAsync<string>("() => blazorMonaco.editor.getEditor('markdown-text-editor', true).getValue()");
        await page.EvaluateAsync("url => Blazor.navigateTo(url)", "/browser");
        await page.GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true }).ClickAsync();

        // Assert
        Assert.Contains(Typed, editorTextAfterKeep);
        await _fixture.WaitForUrlAsync(page, "**/browser", Timeout);
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    private async Task<IPage> OpenPageAsync(string mode)
    {
        await _fixture.WriteDemoPageAsync(PageName, Content);
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync(Timeout);
        if (mode == "Inline")
        {
            await page.SwitchToInlineModeAsync();
        }
        else
        {
            await page.SelectEditModeAsync(mode);
        }

        return page;
    }

    // Waits until the server knows of the change, which it tells the browser for its prompt before a reload.
    private static async Task TypeIntoPageEditorAsync(IPage page)
    {
        var editorContent = page.Locator(".monaco-editor .view-lines");
        await Assertions.Expect(editorContent).ToBeVisibleAsync(new() { Timeout = Timeout });
        await editorContent.ClickAsync();
        await page.Keyboard.PressAsync("ControlOrMeta+End");
        await page.Keyboard.TypeAsync(Typed);
        await page.WaitForFunctionAsync(AsksBeforeUnload, null, new() { Timeout = Timeout });
    }
}
