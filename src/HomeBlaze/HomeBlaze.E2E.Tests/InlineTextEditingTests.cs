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
    private const string LayoutPageName = "TextLayout.md";

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
    // cells, in the footer of a card, and raw HTML, which cannot be edited as text.
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
    public async Task WhenTextIsInsertedAtAnInsertLine_ThenItStandsThereInTheFileAndIsSelected()
    {
        // Arrange: the lines of the page stand before the title, the first paragraph, the card and the last paragraph.
        var page = await OpenPageInInlineModeAsync();
        var lineButton = page.GetByRole(AriaRole.Button, new() { Name = "Insert at position 3 of the page", Exact = true });

        // Act
        await lineButton.ClickAsync();
        await page.GetByTestId("insert-text-item").ClickAsync();
        await page.GetByTestId("markdown-text-input").FillAsync("Inserted text.");
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
        var input = page.GetByTestId("markdown-text-input");
        await Assertions.Expect(input).ToHaveValueAsync("Last paragraph on the page.", new() { Timeout = Timeout });
        await _fixture.WriteDemoPageAsync(PageName, Content.Replace("Last paragraph on the page.", "Changed elsewhere."));

        // Act
        await input.FillAsync("Mine.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("markdown-text-refusal")).ToContainTextAsync("Copy your text", new() { Timeout = Timeout });
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

        // Assert: a grid cell clips its content and has no padding, so there a button at an edge stands over the corner of
        // the block beside it, and only the center of a block must stay free.
        var findings = await fragment.EvaluateAsync<string[]>(
            """
            (fragment, coversCorners) => {
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
                        findings.push(`${button.title} is ${box.height} pixels high`);
                    }

                    const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
                    if (!button.contains(hit)) {
                        findings.push(`${button.title} is not hit at its center, ${hit?.className} is`);
                    }

                    for (const block of blocks) {
                        const other = block.getBoundingClientRect();
                        const width = Math.min(box.right, other.right) - Math.max(box.left, other.left);
                        const height = Math.min(box.bottom, other.bottom) - Math.max(box.top, other.top);
                        const centerX = other.left + other.width / 2;
                        const centerY = other.top + other.height / 2;
                        const coversCenter = box.left <= centerX && centerX <= box.right && box.top <= centerY && centerY <= box.bottom;
                        if (width > 0.5 && height > 0.5 && (!coversCorners || coversCenter)) {
                            findings.push(`${button.title} covers ${width}x${height} pixels of "${block.textContent.trim()}"`);
                        }
                    }
                }

                return findings;
            }
            """,
            content.EndsWith("in a grid cell", StringComparison.Ordinal));
        Assert.True(findings.Length == 0, string.Join("; ", findings));
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
    public async Task WhenTextCannotBeEditedAsText_ThenItIsNotASelectableBlock()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(LayoutPageName, LayoutContent);

        // Act
        var rawHtml = page.Locator(".raw-html-block");
        await Assertions.Expect(rawHtml).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        await Assertions.Expect(page.Locator(".markdown-text:has(.raw-html-block)")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selectable:has(.raw-html-block)")).ToHaveCountAsync(0);
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

    private static ILocator GetTextWrapper(IPage page, string text) =>
        page.Locator(".subject-component-wrapper.selectable:has(> .markdown-text)").Filter(new() { HasText = text });
}
