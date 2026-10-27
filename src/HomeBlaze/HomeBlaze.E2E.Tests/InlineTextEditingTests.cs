using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Namotion.Interceptor.Testing;

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
    private const string LayoutPageName = "TextLayout.md";
    private const string EmptyContentPageName = "EmptyContent.md";
    private const string TextEditorId = "markdown-text-editor";

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

    // Text and blocks in a card, a tab and a flow item, with a heading right before a block, in a flow and a tab that fill grid
    // cells, in the footer of a card, and raw HTML.
    private const string LayoutContent =
        "---\n"
        + "title: Text Layout Demo\n"
        + "---\n"
        + "\n"
        + "# Text Layout Demo\n"
        + "\n"
        + ":: HomeBlaze.Components.Card Title=\"Layout Card\"\n"
        + "One line in the card.\n"
        + "\n"
        + "#### Heading in the card\n"
        + "\n"
        + "::: HomeBlaze.Components.Card Title=\"Inner Card\"\n"
        + "Inner text.\n"
        + ":::\n"
        + "\n"
        + "Last line in the card.\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.TabLayout\n"
        + "::: Tab Title=\"Layout Tab\"\n"
        + "Text in the tab.\n"
        + "\n"
        + ":::: HomeBlaze.Components.Card Title=\"Tab Card\"\n"
        + "In the tab card.\n"
        + "::::\n"
        + "\n"
        + "Text at the end of the tab.\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.FlowLayout MinWidth=300\n"
        + "::: Item\n"
        + "Text in the flow item.\n"
        + "\n"
        + ":::: HomeBlaze.Components.Card Title=\"Flow Card\"\n"
        + "In the flow card.\n"
        + "::::\n"
        + "\n"
        + "Text at the end of the flow item.\n"
        + "::: Item\n"
        + "Second item.\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.GridLayout Rows=1 Columns=2\n"
        + "::: Cell Row=0 Column=0\n"
        + ":::: HomeBlaze.Components.FlowLayout MinWidth=100\n"
        + "::::: Item\n"
        + "Text in the cell flow.\n"
        + "\n"
        + ":::::: HomeBlaze.Components.Card Title=\"Cell Flow Card\"\n"
        + "In the cell flow card.\n"
        + "::::::\n"
        + "::: Cell Row=0 Column=1\n"
        + ":::: HomeBlaze.Components.TabLayout\n"
        + "::::: Tab Title=\"Cell Tab\"\n"
        + "Text in the cell tab.\n"
        + "\n"
        + ":::::: HomeBlaze.Components.Card Title=\"Cell Tab Card\"\n"
        + "In the cell tab card.\n"
        + "::::::\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Samples.Motor#layoutmotor Name=LayoutMotor TargetSpeed=1000 SimulationInterval=00:00:01\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.Card Title=\"Footer Card\"\n"
        + "Body of the footer card.\n"
        + "::: Footer\n"
        + ":::: HomeBlaze.Components.OperationButton Path=layoutmotor/EmergencyStop Color=Error Variant=Outlined\n"
        + ":::: HomeBlaze.Components.Control Path=layoutmotor/GetDiagnostics\n"
        + "::\n"
        + "\n"
        + "<div class=\"raw-html-block\">Raw HTML.</div>\n";

    // Raw HTML that shows something between two paragraphs, and an HTML comment, which shows nothing.
    private const string RawHtmlPageName = "RawHtml.md";

    private const string RawHtmlContent =
        "---\n"
        + "title: Raw HTML Demo\n"
        + "---\n"
        + "\n"
        + "# Raw HTML Demo\n"
        + "\n"
        + "Paragraph before the HTML.\n"
        + "\n"
        + "<div class=\"visible-html-block\">Visible HTML.</div>\n"
        + "\n"
        + "<!-- A comment shows nothing. -->\n"
        + "\n"
        + "Paragraph after the HTML.\n";

    // An empty Alert, whose body has no content of its own, and an empty item of a flow.
    private const string EmptyContent =
        "---\n"
        + "title: Empty Content Demo\n"
        + "---\n"
        + "\n"
        + "# Empty Content Demo\n"
        + "\n"
        + ":: HomeBlaze.Components.Alert\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.FlowLayout MinWidth=200\n"
        + "::: Item\n"
        + "First item.\n"
        + ":::\n"
        + "::: Item\n"
        + ":::\n"
        + "::\n";

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
        await ExpectTextEditorValueAsync(page, "First paragraph on the page.");
        await SetTextEditorValueAsync(page, "Changed *paragraph*.");
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
        await SetTextEditorValueAsync(page, "Changed body.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains(":: HomeBlaze.Components.Card Title=\"Text Card\"\nChanged body.\n::\n") &&
                    text.Contains("\nFirst paragraph on the page.\n"),
            "The card body was not changed.");
        await Assertions.Expect(GetTextWrapper(page, "Changed body.")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenATextBlockInACardIsMovedUp_ThenItStaysSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(LayoutPageName, LayoutContent);
        await page.GetCard("Layout Card").Locator(".mud-card-header").First.SelectWidgetAsync();
        var actionBar = await SelectTextAsync(page, "Last line in the card");

        // Act
        await actionBar.GetByTestId("move-up-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            LayoutPageName,
            text => text.IndexOf("Last line in the card.", StringComparison.Ordinal) < text.IndexOf("Title=\"Inner Card\"", StringComparison.Ordinal),
            "The text was not moved above the inner card.");
        await Assertions.Expect(GetTextWrapper(page, "Last line in the card")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTextIsInsertedAtAnInsertLineOfASelectedCard_ThenItIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        await page.GetCard("Text Card").Locator(".mud-card-header").SelectWidgetAsync();
        var button = page.GetCard("Text Card").GetByRole(AriaRole.Button, new() { Name = "Insert at the end of", Exact = false });

        // Act
        await button.ClickAsync();
        await page.GetByTestId("insert-text-item").ClickAsync();
        await SetTextEditorValueAsync(page, "Inserted in the card.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("Card body text.\n\nInserted in the card.\n"),
            "The text was not inserted into the card.");
        await Assertions.Expect(GetTextWrapper(page, "Inserted in the card.")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenATextBlockIsMovedWithTheKeyboard_ThenTheMovedBlockHasTheFocus()
    {
        // Arrange: Tab goes from the selected block to the first button of its bar.
        var page = await OpenPageInInlineModeAsync();
        await SelectTextAsync(page, "Last paragraph");
        await GetTextWrapper(page, "Last paragraph").FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(page.GetWidgetActionBar().GetByTestId("move-up-button")).ToBeFocusedAsync(new() { Timeout = Timeout });

        // Act
        await page.Keyboard.PressAsync("Enter");

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("First paragraph on the page.\n\nLast paragraph on the page.\n\n:: HomeBlaze.Components.Card"),
            "The text was not moved above the card.");
        await Assertions.Expect(GetTextWrapper(page, "Last paragraph")).ToBeFocusedAsync(new() { Timeout = Timeout });
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
    public async Task WhenATextBlockIsMovedUpPastAWidgetAndDownAgain_ThenTheFileHasEachOrderAndTheTextStaysSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "Last paragraph");

        // Act
        await actionBar.GetByTestId("move-up-button").ClickAsync();
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("First paragraph on the page.\n\nLast paragraph on the page.\n\n:: HomeBlaze.Components.Card"),
            "The text was not moved above the card.");
        await Assertions.Expect(GetTextWrapper(page, "Last paragraph")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        await page.GetWidgetActionBar().GetByTestId("move-down-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(PageName, text => text == Content, "The text was not moved below the card again.");
        await Assertions.Expect(GetTextWrapper(page, "Last paragraph")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        await Assertions.Expect(page.GetWidgetActionBar().GetByTestId("move-up-button")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.GetWidgetActionBar().GetByTestId("move-down-button")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTextIsInsertedThroughThePlaceholderOfAnEmptyItem_ThenTheItemHoldsItAndThePlaceholderIsGone()
    {
        // Arrange: the text of the first item selects the flow, whose items then show their insert lines.
        var page = await OpenPageInInlineModeAsync(EmptyContentPageName, EmptyContent);
        await page.GetByText("First item.", new() { Exact = true }).SelectWidgetAsync();
        var emptyItem = page.GetByTestId("flow-item").Nth(1);
        var placeholder = emptyItem.GetByTestId("content-placeholder");
        await Assertions.Expect(placeholder).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(emptyItem.GetByTestId("block-insert-line")).ToHaveCountAsync(1);

        // Act
        await placeholder.GetByRole(AriaRole.Button).ClickAsync();
        await page.GetByTestId("insert-text-item").ClickAsync();
        await SetTextEditorValueAsync(page, "Filled item.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            EmptyContentPageName,
            text => text.Contains("::: Item\n\nFilled item.\n\n:::\n::\n"),
            "The text was not written into the empty item.");
        await Assertions.Expect(emptyItem.GetByText("Filled item.", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("content-placeholder")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenThePlaceholderOfAnEmptyItemIsDoubleClicked_ThenNoDialogOpens()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(EmptyContentPageName, EmptyContent);
        await page.GetByText("First item.", new() { Exact = true }).SelectWidgetAsync();
        var button = page.GetByTestId("flow-item").Nth(1).GetByTestId("content-placeholder").GetByRole(AriaRole.Button);

        // Act
        await button.DblClickAsync();

        // Assert: the dialog of new text, which the menu then opens, is the only one, and a dialog that the double click
        // opened would have come first.
        await ExpectOnlyTheTextDialogAfterAnInsertAsync(page, button);
    }

    [Fact]
    public async Task WhenAnInsertLineInASelectedCardIsDoubleClicked_ThenNoDialogOpens()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        await page.GetCard("Text Card").Locator(".mud-card-header").SelectWidgetAsync();
        var button = page.GetCard("Text Card").GetByRole(AriaRole.Button, new() { Name = "Insert at the end of", Exact = false });

        // Act
        await button.DblClickAsync();

        // Assert
        await ExpectOnlyTheTextDialogAfterAnInsertAsync(page, button);
    }

    [Fact]
    public async Task WhenTextIsInsertedAtAnInsertLine_ThenItStandsThereInTheFileAndIsSelected()
    {
        // Arrange: the lines of the page stand before the title, the first paragraph, the card and the last paragraph.
        var page = await OpenPageInInlineModeAsync();
        var lineButton = page.GetByRole(AriaRole.Button, new() { Name = "Insert at position 3 of the page", Exact = true });

        // Act
        await lineButton.ClickAsync();
        await page.GetByTestId("insert-text-item").ClickAsync();
        await SetTextEditorValueAsync(page, "Inserted text.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("First paragraph on the page.\n\nInserted text.\n\n:: HomeBlaze.Components.Card"),
            "The text was not inserted before the card.");
        await Assertions.Expect(GetTextWrapper(page, "Inserted text")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTheTextChangedWhileItsDialogWasOpen_ThenTheSaveIsRefusedAndTheDialogKeepsTheInput()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "Last paragraph");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "Last paragraph on the page.");
        await _fixture.WriteDemoPageAsync(PageName, Content.Replace("Last paragraph on the page.", "Changed elsewhere."));

        // Act
        await SetTextEditorValueAsync(page, "Mine.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("markdown-text-refusal")).ToContainTextAsync("Copy your text", new() { Timeout = Timeout });
        await ExpectTextEditorValueAsync(page, "Mine.");
        var text = (await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(PageName))).Replace("\r\n", "\n");
        Assert.Contains("Changed elsewhere.", text);
        Assert.DoesNotContain("Mine.", text);
    }

    [Fact]
    public async Task WhenTheTextDialogOpens_ThenItsEditorFillsTheDialogAndWhatIsTypedAtItsEndIsSavedWithControlS()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "First paragraph");

        // Act
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "First paragraph on the page.");
        await page.WaitForFunctionAsync(
            $"() => blazorMonaco.editor.getEditor('{TextEditorId}', true)?.hasTextFocus() === true", null, new() { Timeout = Timeout });
        await page.WaitForFunctionAsync(
            """
            () => {
                const editor = document.querySelector('[data-testid="markdown-text-input"]');
                const content = document.querySelector('.mud-dialog-content');
                return editor && content && editor.getBoundingClientRect().height > window.innerHeight * 0.5
                    && content.scrollHeight - content.clientHeight <= 0;
            }
            """, null, new() { Timeout = Timeout });
        await page.Keyboard.TypeAsync(" More.");
        var editorHeight = (await page.GetByTestId("markdown-text-input").BoundingBoxAsync())!.Height;
        var contentOverflow = await page.Locator(".mud-dialog-content").EvaluateAsync<double>("content => content.scrollHeight - content.clientHeight");
        await page.Keyboard.PressAsync("ControlOrMeta+s");

        // Assert
        Assert.True(editorHeight > page.ViewportSize!.Height * 0.5, $"The editor is {editorHeight} pixels high in a window of {page.ViewportSize.Height}.");
        Assert.True(contentOverflow <= 0, $"The content of the dialog scrolls by {contentOverflow} pixels.");
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("# Text Editing Demo\n\nFirst paragraph on the page. More.\n\n:: HomeBlaze.Components.Card"),
            "The typed text was not saved.");
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenEscapeIsPressedAfterAChange_ThenTheDialogClosesWithoutSavingOnlyOnceTheDiscardIsConfirmed()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "Last paragraph");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "Last paragraph on the page.");
        await WaitForTextEditorFocusAsync(page);
        await page.Keyboard.TypeAsync(" Unsaved.");

        // Act
        await page.Keyboard.PressAsync("Escape");
        await page.GetByRole(AriaRole.Button, new() { Name = "Keep editing", Exact = true }).ClickAsync();
        await ExpectTextEditorValueAsync(page, "Last paragraph on the page. Unsaved.");
        await page.GetByTestId("markdown-text-cancel").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
        var text = (await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(PageName))).Replace("\r\n", "\n");
        Assert.DoesNotContain("Unsaved.", text);
    }

    [Fact]
    public async Task WhenKeepEditingIsChosenAfterEscape_ThenTheEditorHasTheFocusAgain()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "Last paragraph");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "Last paragraph on the page.");
        await WaitForTextEditorFocusAsync(page);
        await page.Keyboard.TypeAsync(" One.");
        await page.Keyboard.PressAsync("Escape");

        // Act
        await page.GetByRole(AriaRole.Button, new() { Name = "Keep editing", Exact = true }).ClickAsync();

        // Assert
        await WaitForTextEditorFocusAsync(page);
        await page.Keyboard.TypeAsync(" Two.");
        await ExpectTextEditorValueAsync(page, "Last paragraph on the page. One. Two.");
    }

    [Fact]
    public async Task WhenEscapeIsPressedWithTheFocusOnTheTitleOfTheDialog_ThenTheDialogCloses()
    {
        // Arrange: a click on the title takes the focus out of the editor.
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "Last paragraph");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "Last paragraph on the page.");
        await WaitForTextEditorFocusAsync(page);
        await page.GetByRole(AriaRole.Dialog).Locator(".mud-dialog-title").ClickAsync();
        await page.WaitForFunctionAsync(
            $"() => !blazorMonaco.editor.getEditor('{TextEditorId}', true)?.hasTextFocus()", null, new() { Timeout = Timeout });

        // Act
        await page.Keyboard.PressAsync("Escape");

        // Assert
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenATextBlockIsEditedThroughItsDialogWithTheKeyboard_ThenTheChangedBlockHasTheFocus()
    {
        // Arrange: Enter selects the focused block, and a second Enter opens its dialog.
        var page = await OpenPageInInlineModeAsync();
        var wrapper = GetTextWrapper(page, "First paragraph");
        await Assertions.Expect(wrapper).ToBeVisibleAsync(new() { Timeout = Timeout });
        await wrapper.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(wrapper).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        await page.Keyboard.PressAsync("Enter");
        await ExpectTextEditorValueAsync(page, "First paragraph on the page.");
        await WaitForTextEditorFocusAsync(page);

        // Act
        await page.Keyboard.TypeAsync(" Typed.");
        await page.Keyboard.PressAsync("ControlOrMeta+s");

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => text.Contains("\nFirst paragraph on the page. Typed.\n"), "The paragraph was not saved.");
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(GetTextWrapper(page, "Typed.")).ToBeFocusedAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenATextDialogWasClosed_ThenItsEditorIsGoneAndControlSInTheSourceEditorSavesThePage()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var editorCount = await page.EvaluateAsync<int>("() => window.monaco?.editor.getEditors().length ?? 0");
        // The last paragraph, whose bar does not cover the menu of the modes.
        var actionBar = await SelectTextAsync(page, "Last paragraph");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "Last paragraph on the page.");
        await WaitForTextEditorFocusAsync(page);
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await page.WaitForFunctionAsync(
            "count => (window.monaco?.editor.getEditors().length ?? 0) === count", editorCount, new() { Timeout = Timeout });
        await page.SelectEditModeAsync("Source");
        var editorContent = page.Locator(".monaco-editor .view-lines");
        await Assertions.Expect(editorContent).ToBeVisibleAsync(new() { Timeout = Timeout });
        await editorContent.ClickAsync();
        await page.Keyboard.PressAsync("ControlOrMeta+End");
        await page.Keyboard.TypeAsync("Saved by the shortcut.");
        await Assertions.Expect(editorContent).ToContainTextAsync("Saved by the shortcut.", new() { Timeout = Timeout });

        // Act
        await page.Keyboard.PressAsync("ControlOrMeta+s");

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("Last paragraph on the page.\nSaved by the shortcut."),
            "The shortcut did not save the page.");
    }

    [Fact]
    public async Task WhenEscapeIsPressedWithoutAChange_ThenTheDialogClosesAtOnce()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "Last paragraph");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "Last paragraph on the page.");
        await WaitForTextEditorFocusAsync(page);

        // Act
        await page.Keyboard.PressAsync("Escape");

        // Assert
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAWidgetIsCreatedInTheTextDialog_ThenItIsWrittenAtTheCursorAndSelectedAfterTheSave()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "First paragraph");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "First paragraph on the page.");
        await SetTextEditorValueAsync(page, "Before the widget.\n\nAfter the widget.");
        await SetTextEditorCursorAsync(page, 3);

        // Act
        await CreateAlertInTextDialogAsync(page, "textalert");
        await ExpectTextEditorToContainAsync(page, ":: HomeBlaze.Components.Alert#textalert");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("# Text Editing Demo\n\nBefore the widget.\n\n:: HomeBlaze.Components.Alert#textalert") &&
                    text.Contains("::\n\nAfter the widget.\n\n:: HomeBlaze.Components.Card"),
            "The alert was not written between the two paragraphs.");
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected .mud-alert")).ToHaveCountAsync(1, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAWidgetIsCreatedInTheTextDialogOfACardBody_ThenItHasOneColonMoreThanTheCard()
    {
        // Arrange: the first click selects the card, and the text of its body is selectable once the card is selected.
        var page = await OpenPageInInlineModeAsync();
        await page.GetCard("Text Card").Locator(".mud-card-header").SelectWidgetAsync();
        var actionBar = await SelectTextAsync(page, "Card body text");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "Card body text.");
        await SetTextEditorCursorAsync(page, 1);

        // Act
        await CreateAlertInTextDialogAsync(page, "cardalert");
        await ExpectTextEditorToContainAsync(page, "::: HomeBlaze.Components.Alert#cardalert");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains(":: HomeBlaze.Components.Card Title=\"Text Card\"\n::: HomeBlaze.Components.Alert#cardalert") &&
                    text.Contains(":::\n\nCard body text.\n::\n"),
            "The alert was not written into the card with three colons.");
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected .mud-alert")).ToHaveCountAsync(1, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAWidgetOfThePageIsPastedIntoTheTextOfACardBody_ThenItIsWrittenWithOneColonMoreThanTheCard()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        await page.GetCard("Text Card").Locator(".mud-card-header").SelectWidgetAsync();
        var actionBar = await SelectTextAsync(page, "Card body text");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "Card body text.");

        // Act
        await SetTextEditorValueAsync(page, "Card body text.\n\n:: HomeBlaze.Components.Alert#pastedalert\n::");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("Card body text.\n\n::: HomeBlaze.Components.Alert#pastedalert\n:::\n::\n"),
            "The pasted alert was not written into the card with three colons.");
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTheTextDialogIsOpenOnAPhone_ThenItsTextAreaHasTheFocusAndSavesWithControlS()
    {
        // Arrange: Monaco does not support the browsers of phones, so a device whose pointer is coarse gets a text area.
        await _fixture.WriteDemoPageAsync(PageName, Content);
        var page = await _fixture.CreatePageAsync(
            new BrowserNewContextOptions { HasTouch = true, IsMobile = true, ViewportSize = new() { Width = 375, Height = 667 } });
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitUntilInteractiveAsync(Timeout);
        await page.SwitchToInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "First paragraph");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        var textArea = page.GetByRole(AriaRole.Dialog).GetByTestId("markdown-source-text");
        await Assertions.Expect(textArea).ToHaveValueAsync("First paragraph on the page.", new() { Timeout = Timeout });
        await Assertions.Expect(textArea).ToBeFocusedAsync(new() { Timeout = Timeout });

        // Act
        await page.Keyboard.TypeAsync(" More.");
        await page.Keyboard.PressAsync("ControlOrMeta+s");

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("# Text Editing Demo\n\nFirst paragraph on the page. More.\n\n:: HomeBlaze.Components.Card"),
            "The typed text was not saved.");
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTextIsTypedInTheTextDialog_ThenTheDialogIsNotRenderedForEveryCharacter()
    {
        // Arrange: the server sends a render batch for every render, and calls update of the module of the editor once it
        // decorated the text of a version of the editor model.
        await _fixture.WriteDemoPageAsync(PageName, Content);
        var page = await _fixture.CreatePageAsync();
        var renderBatchCount = 0;
        var decoratedVersions = new ConcurrentQueue<string>();
        page.WebSocket += (_, webSocket) => webSocket.FrameReceived += (_, frame) =>
        {
            var text = frame.Binary is { } binary ? Encoding.Latin1.GetString(binary) : frame.Text ?? string.Empty;
            Interlocked.Add(ref renderBatchCount, Regex.Count(text, "JS\\.RenderBatch"));
            foreach (Match match in Regex.Matches(text, $"update.{{0,8}}\\[\"{TextEditorId}\",(\\d+),"))
            {
                decoratedVersions.Enqueue(match.Groups[1].Value);
            }
        };

        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitUntilInteractiveAsync(Timeout);
        await page.SwitchToInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "First paragraph");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "First paragraph on the page.");
        await WaitForTextEditorFocusAsync(page);
        await page.Keyboard.TypeAsync("a");
        await WaitForDecoratedVersionAsync(page, decoratedVersions);
        var renderBatchCountBefore = Volatile.Read(ref renderBatchCount);

        // Act
        await page.Keyboard.TypeAsync("bcdefghi");
        await WaitForDecoratedVersionAsync(page, decoratedVersions);

        // Assert: a render for every character would be at least eight batches.
        var renderBatchCountWhileTyping = Volatile.Read(ref renderBatchCount) - renderBatchCountBefore;
        Assert.True(renderBatchCountWhileTyping < 4, $"{renderBatchCountWhileTyping} render batches arrived while eight characters were typed.");
    }

    [Fact]
    public async Task WhenEscapeClosesTheSuggestionsOfTheEditor_ThenTheDialogStaysOpenToSave()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "Last paragraph");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "Last paragraph on the page.");
        await WaitForTextEditorFocusAsync(page);
        await page.Keyboard.TypeAsync(" pa");
        await page.EvaluateAsync($"() => blazorMonaco.editor.getEditor('{TextEditorId}').trigger('test', 'editor.action.triggerSuggest', {{}})");
        var suggestions = page.Locator(".suggest-widget.visible");
        await Assertions.Expect(suggestions).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(suggestions).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert: a question whether to discard the change would have covered the dialog or held its save back.
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => text.Contains("Last paragraph on the page. pa\n"), "The text was not saved after Escape.");
    }

    [Fact]
    public async Task WhenTheBackdropOfTheTextDialogIsClicked_ThenTheDialogStaysOpenToSaveWhatWasTyped()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectTextAsync(page, "Last paragraph");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "Last paragraph on the page.");
        await WaitForTextEditorFocusAsync(page);
        await page.Keyboard.TypeAsync(" Typed.");
        var dialog = (await page.Locator(".mud-dialog.markdown-text-dialog").BoundingBoxAsync())!;

        // Act: beside the dialog, or above it when it is as wide as the window.
        if (dialog.X > 10)
        {
            await page.Mouse.ClickAsync(dialog.X / 2, dialog.Y + dialog.Height / 2);
        }
        else
        {
            await page.Mouse.ClickAsync(dialog.X + dialog.Width / 2, dialog.Y / 2);
        }

        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert: the save needs the dialog that a closing click would have removed.
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => text.Contains("Last paragraph on the page. Typed.\n"), "The typed text was not saved.");
    }

    [Fact]
    public async Task WhenTheTextIsSavedUnchanged_ThenTheDialogClosesWithoutWritingThePage()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var path = _fixture.GetDemoPagePath(PageName);
        var actionBar = await SelectTextAsync(page, "Last paragraph");
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "Last paragraph on the page.");
        var lastWriteTime = File.GetLastWriteTimeUtc(path);

        // Act
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert: the save returns after its write, before the dialog closes.
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
        Assert.Equal(lastWriteTime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task WhenATextBlockIsDoubleClicked_ThenItsTextDialogOpens()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var wrapper = GetTextWrapper(page, "First paragraph");
        await Assertions.Expect(wrapper).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await wrapper.DblClickAsync(new() { Force = true });

        // Assert
        await ExpectTextEditorValueAsync(page, "First paragraph on the page.");
    }

    [Fact]
    public async Task WhenATextBlockInASelectedCardIsDoubleClicked_ThenOnlyItsTextDialogOpens()
    {
        // Arrange: the text of a card body is selectable once the card is selected.
        var page = await OpenPageInInlineModeAsync();
        await page.GetCard("Text Card").Locator(".mud-card-header").SelectWidgetAsync();
        var wrapper = GetTextWrapper(page, "Card body text");
        await Assertions.Expect(wrapper).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await wrapper.DblClickAsync(new() { Force = true });

        // Assert: the dialog of the text opened, not the one of the card.
        await ExpectTextEditorValueAsync(page, "Card body text.");
        await Assertions.Expect(page.GetByTestId("subject-edit-dialog-type")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task WhenTheBodyTextOfACardThatIsNotSelectedIsDoubleClicked_ThenOnlyTheTextDialogOpensAndNoWordStaysSelected()
    {
        // Arrange: the first click of the double click selects the card, and the text is selectable only after that.
        var page = await OpenPageInInlineModeAsync();
        var text = page.GetCard("Text Card").GetByText("Card body text.", new() { Exact = true });
        await Assertions.Expect(text).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await text.DblClickAsync(new() { Force = true });

        // Assert
        await ExpectTextEditorValueAsync(page, "Card body text.");
        await Assertions.Expect(page.GetByTestId("subject-edit-dialog-type")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(1);
        Assert.Equal(string.Empty, await page.EvaluateAsync<string>("() => window.getSelection()?.toString() ?? ''"));
    }

    [Fact]
    public async Task WhenALinkInATextBlockOfACardThatIsNotSelectedIsClicked_ThenTheCardIsSelectedAndThePageStays()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(PageName, Content.Replace("Card body text.", "Card body [link](https://example.com/).", StringComparison.Ordinal));
        var link = page.GetCard("Text Card").GetByRole(AriaRole.Link, new() { Name = "link" });
        await Assertions.Expect(link).ToBeVisibleAsync(new() { Timeout = Timeout });
        var address = page.Url;

        // Act
        await link.ClickAsync(new() { Force = true });

        // Assert
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected:has(> [data-testid='card'])")).ToHaveCountAsync(1, new() { Timeout = Timeout });
        Assert.Equal(address, page.Url);
    }

    [Fact]
    public async Task WhenATextBlockIsDoubleClickedInViewMode_ThenNoDialogOpens()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        await page.SelectEditModeAsync("View", Timeout);
        var paragraph = page.Locator("p").Filter(new() { HasText = "First paragraph" });
        await Assertions.Expect(paragraph).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await paragraph.DblClickAsync();

        // Assert
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenThePageIsViewed_ThenItsTextIsNotWrapped()
    {
        // Arrange: in Inline mode the text blocks have wrappers of their own, which View mode does not render.
        var page = await OpenPageInInlineModeAsync();
        await Assertions.Expect(page.Locator(".subject-component-wrapper.text-block").First).ToBeAttachedAsync(new() { Timeout = Timeout });

        // Act
        await page.SelectEditModeAsync("View", Timeout);

        // Assert
        await Assertions.Expect(page.Locator(".markdown-text")).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".subject-component-wrapper.text-block")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenATextBlockIsSelected_ThenItsOutlineAndItsBarAreGreenUnlikeThoseOfAWidget()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var cardWrapper = page.Locator(".subject-component-wrapper.selectable:has(> [data-testid='card'])")
            .Filter(new() { Has = page.GetByTestId("card-title").GetByText("Text Card", new() { Exact = true }) });

        // Act
        await SelectTextAsync(page, "First paragraph");
        await Assertions.Expect(GetTextWrapper(page, "First paragraph")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        var textOutline = await GetOutlineColorAsync(GetTextWrapper(page, "First paragraph"));
        await Assertions.Expect(page.GetWidgetActionBar()).ToHaveClassAsync(new Regex(@"\btext-block-bar\b"), new() { Timeout = Timeout });
        await page.GetCard("Text Card").Locator(".mud-card-header").SelectWidgetAsync();
        await Assertions.Expect(cardWrapper).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        var cardOutline = await GetOutlineColorAsync(cardWrapper);

        // Assert
        Assert.True(IsGreen(textOutline), $"The outline of the selected text is {textOutline}.");
        Assert.False(IsGreen(cardOutline), $"The outline of the selected card is {cardOutline}.");
        await Assertions.Expect(page.GetWidgetActionBar()).Not.ToHaveClassAsync(new Regex(@"\btext-block-bar\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAWidgetOrATextBlockIsSelected_ThenItsBarHasABorderInTheColorOfItsOutline()
    {
        // Arrange: the server runs in dark mode, which lightens both colors.
        var page = await OpenPageInInlineModeAsync();
        var cardWrapper = page.Locator(".subject-component-wrapper.selectable:has(> [data-testid='card'])")
            .Filter(new() { Has = page.GetByTestId("card-title").GetByText("Text Card", new() { Exact = true }) });
        var primary = await page.ResolveColorAsync("var(--mud-palette-primary-lighten)");
        var success = await page.ResolveColorAsync("var(--mud-palette-success-lighten)");

        // Act
        var cardBar = await page.GetCard("Text Card").Locator(".mud-card-header").SelectWidgetAsync();
        await Assertions.Expect(cardBar).Not.ToHaveClassAsync(new Regex(@"\btext-block-bar\b"), new() { Timeout = Timeout });
        var cardColors = new[] { await GetOutlineColorAsync(cardWrapper), await GetBorderColorAsync(cardBar) };
        var textBar = await SelectTextAsync(page, "First paragraph");
        await Assertions.Expect(textBar).ToHaveClassAsync(new Regex(@"\btext-block-bar\b"), new() { Timeout = Timeout });
        var textColors = new[] { await GetOutlineColorAsync(GetTextWrapper(page, "First paragraph")), await GetBorderColorAsync(textBar) };

        // Assert
        Assert.Equal(new[] { primary, primary }, cardColors);
        Assert.Equal(new[] { success, success }, textColors);
    }

    [Fact]
    public async Task WhenAOneLineParagraphInASelectedCardIsClickedInItsMiddle_ThenTheParagraphIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(LayoutPageName, LayoutContent);
        await page.GetCard("Layout Card").Locator(".mud-card-header").First.SelectWidgetAsync();
        var paragraph = GetTextWrapper(page, "One line in the card");
        await Assertions.Expect(paragraph).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await paragraph.ClickAsync(new() { Force = true });

        // Assert
        await Assertions.Expect(paragraph).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("create-subject-dialog")).ToHaveCountAsync(0);
    }

    [Theory]
    [InlineData("card")]
    [InlineData("tab")]
    [InlineData("flow item")]
    [InlineData("page")]
    [InlineData("card footer")]
    [InlineData("flow item in a grid cell")]
    [InlineData("tab in a grid cell")]
    public async Task WhenAContentIsAtTheSelection_ThenItsInsertButtonsCanBeClickedAndCoverNoBlock(string content)
    {
        // Arrange: the page has a paragraph right after its title.
        var page = content == "page"
            ? await OpenPageInInlineModeAsync()
            : await OpenPageInInlineModeAsync(LayoutPageName, LayoutContent);
        const string Fragment = ":scope > .subject-component-wrapper > .markdown-fragment";
        var cells = page.Locator(".grid-cell-content");

        // Act: the content of a tab or a flow item is at the selection when its layout is selected, and the page when nothing
        // is.
        var (selectionTarget, fragment, lineCount) = content switch
        {
            "card" => (
                page.GetCard("Layout Card").Locator(".mud-card-header").First,
                page.GetCard("Layout Card").Locator(".mud-card-content > .subject-component-wrapper > .markdown-fragment").First,
                5),
            "tab" => (
                page.GetByText("Text in the tab.", new() { Exact = true }),
                page.GetByTestId("tab-panel").First.Locator(Fragment),
                4),
            "flow item" => (
                page.GetByText("Second item.", new() { Exact = true }),
                page.GetByTestId("flow-item").First.Locator(Fragment),
                4),
            "page" => (
                null,
                page.Locator(".markdown-content"),
                5),
            "card footer" => (
                page.GetCard("Footer Card").Locator(".mud-card-header").First,
                page.GetCard("Footer Card").Locator(".mud-card-actions > .subject-component-wrapper > .markdown-fragment"),
                3),
            "flow item in a grid cell" => (
                cells.Nth(0).GetByText("Text in the cell flow.", new() { Exact = true }),
                cells.Nth(0).GetByTestId("flow-item").First.Locator(Fragment),
                3),
            _ => (
                cells.Nth(1).GetByText("Text in the cell tab.", new() { Exact = true }),
                cells.Nth(1).GetByTestId("tab-panel").Locator(Fragment),
                3)
        };
        if (selectionTarget is not null)
        {
            await selectionTarget.SelectWidgetAsync();
        }

        await Assertions.Expect(fragment.Locator(":scope > [data-testid='block-insert-line']")).ToHaveCountAsync(lineCount, new() { Timeout = Timeout });

        // Assert: every button is centered on its line and covers no block, also where a grid cell clips the content.
        var findings = await fragment.EvaluateAsync<string[]>(
            """
            fragment => {
                const findings = [];
                const buttons = [...fragment.querySelectorAll(':scope > [data-testid=block-insert-line] button')];

                // The blocks of a card footer stand below the body, whose blocks are next to the buttons as well.
                const body = fragment.closest('.mud-card-actions')?.parentElement
                    .querySelector(':scope > .mud-card-content > .subject-component-wrapper > .markdown-fragment');
                const blocks = [fragment, body]
                    .filter(container => container)
                    .flatMap(container => [...container.querySelectorAll(':scope > .subject-component-wrapper')])
                    .filter(block => block.getBoundingClientRect().height > 0);
                for (const button of buttons) {
                    // A point outside the window hits nothing.
                    button.scrollIntoView({ block: 'center' });
                    const box = button.getBoundingClientRect();
                    if (box.height > 16.5) {
                        findings.push(`${button.ariaLabel} is ${box.height} pixels high`);
                    }

                    const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
                    if (!button.contains(hit)) {
                        findings.push(`${button.ariaLabel} is not hit at its center, ${hit?.className} is`);
                    }

                    // A line in the row of a card footer stands upright between two blocks of the row.
                    const line = button.closest('[data-testid=block-insert-line]').getBoundingClientRect();
                    const offset = box.left + box.width / 2 - (line.left + line.width / 2);
                    if (Math.abs(offset) > 1) {
                        findings.push(`${button.ariaLabel} is ${offset} pixels beside the center of its line`);
                    }

                    for (const block of blocks) {
                        const other = block.getBoundingClientRect();
                        const width = Math.min(box.right, other.right) - Math.max(box.left, other.left);
                        const height = Math.min(box.bottom, other.bottom) - Math.max(box.top, other.top);
                        if (width > 0.5 && height > 0.5) {
                            findings.push(`${button.ariaLabel} covers ${width}x${height} pixels of "${block.textContent.trim()}"`);
                        }
                    }
                }

                return findings;
            }
            """);
        Assert.True(findings.Length == 0, string.Join("; ", findings));
    }

    [Fact]
    public async Task WhenTextBlocksAreSelectable_ThenEachIsNamedByItsKindAndItsFirstWords()
    {
        // Act
        var page = await OpenPageInInlineModeAsync();

        // Assert
        await Assertions.Expect(GetTextWrapper(page, "Text Editing Demo")).ToHaveAttributeAsync("aria-label", "Heading: Text Editing Demo", new() { Timeout = Timeout });
        await Assertions.Expect(GetTextWrapper(page, "First paragraph")).ToHaveAttributeAsync("aria-label", "Paragraph: First paragraph on the page.");
    }

    [Fact]
    public async Task WhenTheBodyTextOfACardIsClickedFirst_ThenTheCardIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(LayoutPageName, LayoutContent);
        var cardWrapper = page.Locator(".subject-component-wrapper.selectable:has(> [data-testid='card'])")
            .Filter(new() { Has = page.GetByTestId("card-title").GetByText("Layout Card", new() { Exact = true }) });

        // Act
        await page.GetByText("One line in the card.", new() { Exact = true }).SelectWidgetAsync();

        // Assert
        await Assertions.Expect(cardWrapper).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected:has(> .markdown-text)")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenABlockInsideACardIsDeleted_ThenTheCardIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(LayoutPageName, LayoutContent);
        var layoutCard = page.Locator(".subject-component-wrapper.selectable:has(> [data-testid='card'])")
            .Filter(new() { Has = page.GetByTestId("card-title").GetByText("Layout Card", new() { Exact = true }) });
        await page.GetCard("Layout Card").Locator(".mud-card-header").First.SelectWidgetAsync();
        var actionBar = await page.GetCard("Inner Card").Last.Locator(".mud-card-header").SelectWidgetAsync();
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected:has(> [data-testid='card'])")
            .Filter(new() { Has = page.GetByTestId("card-title").GetByText("Inner Card", new() { Exact = true }) })).ToHaveCountAsync(1, new() { Timeout = Timeout });

        // Act
        await actionBar.GetByTestId("delete-block-button").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            LayoutPageName, text => !text.Contains("Inner Card", StringComparison.Ordinal), "The inner card was not deleted.");
        await Assertions.Expect(page.GetByTestId("card-title").GetByText("Inner Card", new() { Exact = true })).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(layoutCard).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenRawHtmlStandsBetweenParagraphs_ThenTheParagraphsCanBeEdited()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(RawHtmlPageName, RawHtmlContent);
        await SelectTextAsync(page, "Paragraph before the HTML");
        var actionBar = await SelectTextAsync(page, "Paragraph after the HTML");

        // Act
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await ExpectTextEditorValueAsync(page, "Paragraph after the HTML.");
        await SetTextEditorValueAsync(page, "Changed after the HTML.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            RawHtmlPageName,
            text => text.EndsWith("<div class=\"visible-html-block\">Visible HTML.</div>\n\n<!-- A comment shows nothing. -->\n\nChanged after the HTML.\n", StringComparison.Ordinal),
            "The paragraph after the HTML was not changed.");
    }

    [Fact]
    public async Task WhenRawHtmlThatShowsSomethingIsSelected_ThenItsBarHasOnlyEditInSource()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(RawHtmlPageName, RawHtmlContent);
        var wrapper = GetTextWrapper(page, "Visible HTML.");

        // Act
        var actionBar = await SelectTextAsync(page, "Visible HTML.");

        // Assert
        await Assertions.Expect(wrapper).ToHaveAttributeAsync("aria-label", "HTML: Visible HTML.");
        await Assertions.Expect(wrapper).ToHaveClassAsync(new Regex(@"\btext-block\b"));
        await Assertions.Expect(actionBar.GetByTestId("edit-in-source-button")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(actionBar.Locator("button")).ToHaveCountAsync(1);

        // The comment shows nothing and is no block to select.
        await Assertions.Expect(page.Locator(".subject-component-wrapper.text-block")).ToHaveCountAsync(4);
    }

    [Fact]
    public async Task WhenTextIsInsertedRightBeforeTheBlockAfterAComment_ThenItGoesBeforeTheComment()
    {
        // Arrange: the comment has an insert line before it and none after it.
        var page = await OpenPageInInlineModeAsync(RawHtmlPageName, RawHtmlContent);
        var lines = page.Locator(".markdown-content > [data-testid='block-insert-line']");
        await Assertions.Expect(lines).ToHaveCountAsync(5, new() { Timeout = Timeout });
        var lineBeforeParagraph = GetTextWrapper(page, "Paragraph after the HTML")
            .Locator("xpath=preceding-sibling::div[@data-testid='block-insert-line'][1]");

        // Act
        await lineBeforeParagraph.GetByRole(AriaRole.Button).ClickAsync();
        await page.GetByTestId("insert-text-item").ClickAsync();
        await SetTextEditorValueAsync(page, "Inserted text.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            RawHtmlPageName,
            text => text.Contains("Visible HTML.</div>\n\nInserted text.\n\n<!-- A comment shows nothing. -->\n\nParagraph after the HTML.\n", StringComparison.Ordinal),
            "The text was not inserted before the comment.");
    }

    [Fact]
    public async Task WhenEditInSourceIsClicked_ThenTheSplitModeShowsTheLinesOfTheHtmlInTheEditor()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(RawHtmlPageName, RawHtmlContent);
        var actionBar = await SelectTextAsync(page, "Visible HTML.");

        // Act
        await actionBar.GetByTestId("edit-in-source-button").ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("edit-splitter")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await WaitForPageEditorSelectionAsync(page, GetLineOf(RawHtmlContent, "visible-html-block"));
    }

    [Fact]
    public async Task WhenRawHtmlThatShowsSomethingIsDoubleClicked_ThenTheSplitModeShowsItsLinesInTheEditor()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(RawHtmlPageName, RawHtmlContent);
        var wrapper = GetTextWrapper(page, "Visible HTML.");
        await Assertions.Expect(wrapper).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await wrapper.DblClickAsync(new() { Force = true });

        // Assert
        await Assertions.Expect(page.GetByTestId("edit-splitter")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await WaitForPageEditorSelectionAsync(page, GetLineOf(RawHtmlContent, "visible-html-block"));
    }

    private Task<IPage> OpenPageInInlineModeAsync() => OpenPageInInlineModeAsync(PageName, Content);

    private async Task<IPage> OpenPageInInlineModeAsync(string pageName, string content)
    {
        await _fixture.WriteDemoPageAsync(pageName, content);
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{pageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync(Timeout);
        await page.SwitchToInlineModeAsync();
        return page;
    }

    // The bar of a widget that was selected before shows until the selection moves, so the wait for the selected text keeps
    // a click on the bar from reaching that widget.
    private static async Task<ILocator> SelectTextAsync(IPage page, string text)
    {
        var wrapper = GetTextWrapper(page, text);
        var actionBar = await wrapper.SelectWidgetAsync();
        await Assertions.Expect(wrapper).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        return actionBar;
    }

    // Opens the dialog of new text through the menu of the insert button. A dialog opened before, such as the edit dialog of
    // the widget around the button, covers the button or stays beside the text dialog.
    private static async Task ExpectOnlyTheTextDialogAfterAnInsertAsync(IPage page, ILocator insertButton)
    {
        await insertButton.ClickAsync();
        await page.GetByTestId("insert-text-item").ClickAsync();
        await ExpectTextEditorValueAsync(page, string.Empty);
        await Assertions.Expect(page.GetByTestId("subject-edit-dialog-type")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(1);
    }

    // The 1-based line of the content that contains the text.
    private static int GetLineOf(string content, string text) => content.Split('\n').ToList().FindIndex(line => line.Contains(text)) + 1;

    // The page editor of the Split mode is the only Monaco editor while no text dialog is open.
    private static Task WaitForPageEditorSelectionAsync(IPage page, int startLine) =>
        page.WaitForFunctionAsync(
            "line => monaco.editor.getEditors()[0]?.getSelection()?.startLineNumber === line",
            startLine,
            new() { Timeout = Timeout });

    private static ILocator GetTextWrapper(IPage page, string text) =>
        page.Locator(".subject-component-wrapper.selectable:has(> .markdown-text)").Filter(new() { HasText = text });

    // The dialog edits its text in Monaco, which has no input value, so the text is read and written through the editor.
    private static async Task ExpectTextEditorValueAsync(IPage page, string expected)
    {
        try
        {
            await page.WaitForFunctionAsync(
                $"expected => window.blazorMonaco?.editor.getEditor('{TextEditorId}', true)?.getValue() === expected",
                expected,
                new() { Timeout = Timeout });
        }
        catch (TimeoutException)
        {
            var actual = await page.EvaluateAsync<string?>($"() => window.blazorMonaco?.editor.getEditor('{TextEditorId}', true)?.getValue() ?? null");
            Assert.Fail($"The text editor has {(actual is null ? "not opened" : $"the text '{actual}'")} and not '{expected}'.");
        }
    }

    // Waits until the server decorated the text of the current version of the editor model, after it handled every change.
    private static async Task WaitForDecoratedVersionAsync(IPage page, ConcurrentQueue<string> decoratedVersions)
    {
        var version = (await page.EvaluateAsync<int>($"() => blazorMonaco.editor.getEditor('{TextEditorId}').getModel().getVersionId()"))
            .ToString(CultureInfo.InvariantCulture);
        await AsyncTestHelpers.WaitUntilAsync(() => decoratedVersions.Contains(version), message: $"The server did not decorate version {version}.");
    }

    private static Task WaitForTextEditorFocusAsync(IPage page) =>
        page.WaitForFunctionAsync(
            $"() => blazorMonaco.editor.getEditor('{TextEditorId}', true)?.hasTextFocus() === true", null, new() { Timeout = Timeout });

    private static async Task SetTextEditorValueAsync(IPage page, string text)
    {
        await page.WaitForFunctionAsync(
            $"() => window.blazorMonaco?.editor.getEditor('{TextEditorId}', true) != null", null, new() { Timeout = Timeout });
        await page.EvaluateAsync($"text => blazorMonaco.editor.getEditor('{TextEditorId}').setValue(text)", text);
    }

    private static Task SetTextEditorCursorAsync(IPage page, int lineNumber) =>
        page.EvaluateAsync($"line => blazorMonaco.editor.getEditor('{TextEditorId}').setPosition({{ lineNumber: line, column: 1 }})", lineNumber);

    private static Task ExpectTextEditorToContainAsync(IPage page, string expected) =>
        page.WaitForFunctionAsync(
            $"expected => window.blazorMonaco?.editor.getEditor('{TextEditorId}', true)?.getValue().includes(expected) === true",
            expected,
            new() { Timeout = Timeout });

    // Create of the text dialog opens the setup dialog above it, which closes once the alert is configured.
    private static async Task CreateAlertInTextDialogAsync(IPage page, string name)
    {
        await page.GetByTestId("markdown-text-create").ClickAsync();
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByTestId("type-search-input").FillAsync("Alert");
        await dialog.GetByTestId("type-row-alert").ClickAsync();
        await dialog.GetByTestId("create-name-input").FillAsync(name);
        await page.GetByTestId("create-button").ClickAsync();
        await Assertions.Expect(dialog).ToBeHiddenAsync(new() { Timeout = Timeout });
    }

    private static Task<string> GetOutlineColorAsync(ILocator element) =>
        element.EvaluateAsync<string>("element => getComputedStyle(element).outlineColor");

    private static Task<string> GetBorderColorAsync(ILocator element) =>
        element.EvaluateAsync<string>("element => getComputedStyle(element).borderTopColor");

    // The success color of both palettes, #007932 and its lightened tone #0dde9c, has its largest channel in green, unlike
    // the primary colors.
    private static bool IsGreen(string color)
    {
        var channels = Regex.Matches(color, @"\d+(?:\.\d+)?")
            .Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture))
            .ToArray();
        return channels.Length >= 3 && channels[1] > channels[0] && channels[1] > channels[2];
    }
}
