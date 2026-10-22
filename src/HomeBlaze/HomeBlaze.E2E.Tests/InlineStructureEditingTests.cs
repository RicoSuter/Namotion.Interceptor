using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Samples;
using HomeBlaze.Storage.Files;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the structure editing of a markdown page in inline edit mode: delete, move and insert blocks, the items
/// of list slots, the widgets that render nothing, the content of a Responsive block and the preview of the Split mode.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class InlineStructureEditingTests
{
    private const int Timeout = 30000;
    private const string PageName = "Structure.md";

    // The server writes the page in its output folder, so a run that ends with a change of the page would change the
    // baseline of the next run if it were read from there.
    private const string Content =
        "---\n"
        + "title: Structure Demo\n"
        + "---\n"
        + "\n"
        + "# Structure Demo\n"
        + "\n"
        + ":: HomeBlaze.Samples.Motor#structuremotor Name=StructureMotor TargetSpeed=1000 SimulationInterval=00:00:01\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.Switch Path=structuremotor/IsPowered\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.ValueList\n"
        + "::: Value Path=structuremotor/TargetSpeed Label=\"Row A\"\n"
        + ":::\n"
        + "::: Value Path=structuremotor/CurrentSpeed Label=\"Row B\"\n"
        + ":::\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.ValueMap#structuremap\n"
        + "::: Case Value=Stopped Text=\"Off\"\n"
        + ":::\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.Responsive To=Xs\n"
        + "::: HomeBlaze.Components.Card Title=\"Phone Card\"\n"
        + "Only on phones.\n"
        + ":::\n"
        + "::\n";

    private readonly PlaywrightFixture _fixture;

    public InlineStructureEditingTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenDeleteIsConfirmed_ThenTheBlockIsRemovedFromThePage()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act: the bar has a button named Delete as well, so the one of the dialog is looked up in the dialog.
        await actionBar.GetByTestId("delete-block-button").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => !text.Contains("HomeBlaze.Components.Switch"), "The switch block was not deleted.");
        await Assertions.Expect(page.GetByTestId("switch")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenDeleteIsCanceled_ThenThePageIsUnchanged()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("delete-block-button").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
        Assert.Equal(Content, (await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(PageName))).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task WhenABlockIsMovedDown_ThenItSwapsWithTheNextBlockInTheFile()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("move-down-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.IndexOf("HomeBlaze.Components.ValueList", StringComparison.Ordinal) <
                    text.IndexOf("HomeBlaze.Components.Switch", StringComparison.Ordinal),
            "The switch was not moved below the value list.");
    }

    [Fact]
    public async Task WhenARowIsMovedDown_ThenTheRowsSwapInTheFile()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectRowAsync(page, "Row A");

        // Act
        await actionBar.GetByTestId("move-down-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.IndexOf("Label=\"Row B\"", StringComparison.Ordinal) < text.IndexOf("Label=\"Row A\"", StringComparison.Ordinal),
            "The rows were not swapped.");
    }

    [Fact]
    public async Task WhenAValueIsAddedToASelectedList_ThenItsRowFollowsTheLastRow()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        await GetValueListWrapper(page).SelectWidgetAsync();

        // Act
        await page.GetByTestId("add-value-button").ClickAsync();
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByLabel("Label", new() { Exact = true }).FillAsync("Row C");
        await page.GetByTestId("create-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => text.Contains("Label=\"Row B\"\n:::\n::: Value Label=\"Row C\""), "The value was not added.");
    }

    [Fact]
    public async Task WhenARowIsSelected_ThenAValueCanBeAddedToItsList()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        await SelectRowAsync(page, "Row A");

        // Act
        await page.GetByTestId("add-value-button").ClickAsync();
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByLabel("Label", new() { Exact = true }).FillAsync("Row C");
        await page.GetByTestId("create-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => text.Contains("Label=\"Row B\"\n:::\n::: Value Label=\"Row C\""), "The value was not added.");
    }

    [Fact]
    public async Task WhenAWidgetInTheContentOfARowIsClickedWhileTheListIsNotSelected_ThenItIsSelectedAndStaysSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(Content.Replace(
            "::: Value Path=structuremotor/TargetSpeed Label=\"Row A\"\n:::\n",
            "::: Value Path=structuremotor/TargetSpeed Label=\"Row A\"\n"
            + ":::: HomeBlaze.Components.Progress Path=structuremotor/TargetSpeed Minimum=0 Maximum=1600\n"
            + "::::\n"
            + ":::\n"));
        var progress = GetWrapperOf(page, "progress");

        // Act
        await progress.SelectWidgetAsync();

        // Assert: the rows become selectable once the selection is in the list, and the progress stays selected then.
        await Assertions.Expect(GetValueRowWrapper(page, "Row A")).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await Assertions.Expect(progress).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected")).ToHaveCountAsync(1, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAListThatIsNotSelectedIsClicked_ThenTheListIsSelectedAndASecondClickSelectsARow()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var list = GetValueListWrapper(page);
        var row = GetValueRowWrapper(page, "Row A");
        await Assertions.Expect(row).ToHaveCountAsync(0, new() { Timeout = Timeout });

        // Act
        await list.SelectWidgetAsync();

        // Assert
        await Assertions.Expect(list).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });

        // Act
        await row.SelectWidgetAsync();

        // Assert
        await Assertions.Expect(row).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        await Assertions.Expect(list).Not.ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        await Assertions.Expect(page.GetWidgetActionBar().GetByTestId("move-down-button")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTheInlineModeIsLeft_ThenTheStructureActionsAreGone()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act
        await page.SelectEditModeAsync("View", Timeout);

        // Assert
        await Assertions.Expect(page.GetByTestId("delete-block-button")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenThePageIsEditedInline_ThenAValueMapIsAChipWithItsName()
    {
        // Arrange & Act
        var page = await OpenPageInInlineModeAsync();

        // Assert
        var chip = page.GetByTestId("invisible-widget-chip").Filter(new() { HasText = "structuremap" });
        await Assertions.Expect(chip).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAValueMapChipIsSelected_ThenItsCasesAreListedAndACaseCanBeAdded()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var chip = page.GetByTestId("invisible-widget-chip").Filter(new() { HasText = "structuremap" });
        await chip.SelectWidgetAsync();
        var cases = page.GetByTestId("value-map-cases");
        await Assertions.Expect(cases.GetByTestId("value-map-case")).ToHaveCountAsync(1, new() { Timeout = Timeout });

        // Act
        await cases.GetByTestId("add-case-button").ClickAsync();
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByLabel("Value", new() { Exact = true }).FillAsync("Running");

        // The actions of a dialog stand outside the element with its test id.
        await page.GetByTestId("create-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => text.Contains("::: Case Value=Stopped Text=\"Off\"\n:::\n::: Case Value=Running"), "The case was not added.");
    }

    [Fact]
    public async Task WhenThePageIsEditedInline_ThenTheChipCoversNoContent()
    {
        // Arrange & Act
        var page = await OpenPageInInlineModeAsync();
        var chip = page.GetByTestId("invisible-widget-chip").Filter(new() { HasText = "structuremap" });
        await Assertions.Expect(chip).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert: neither a line of text, nor a block, nor an insert line of the content around the chips reaches into the chip.
        var coveredContent = await chip.EvaluateAsync<string[]>(
            """
            chip => {
                const box = chip.getBoundingClientRect();
                const stack = chip.closest('.invisible-widget-stack');
                const content = stack.parentElement;
                const overlaps = rectangle => rectangle.width > 0 && rectangle.height > 0 &&
                    rectangle.left < box.right && rectangle.right > box.left && rectangle.top < box.bottom && rectangle.bottom > box.top;
                const covered = [];
                for (const block of content.querySelectorAll(':scope > :is(.subject-component-wrapper, .block-insert-line)')) {
                    if (overlaps(block.getBoundingClientRect())) {
                        covered.push(block.getAttribute('aria-label') ?? 'block');
                    }
                }

                const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT);
                for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                    if (stack.contains(node) || !node.textContent.trim()) {
                        continue;
                    }

                    const range = document.createRange();
                    range.selectNodeContents(node);
                    for (const line of range.getClientRects()) {
                        if (overlaps(line)) {
                            covered.push(node.textContent.trim());
                        }
                    }
                }

                return covered;
            }
            """);
        Assert.Empty(coveredContent);
    }

    [Fact]
    public async Task WhenACaseInTheListOfAValueMapIsDeleted_ThenItIsRemovedFromThePage()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        await page.GetByTestId("invisible-widget-chip").Filter(new() { HasText = "structuremap" }).SelectWidgetAsync();
        var actionBar = await page.GetByTestId("value-map-cases").GetByTestId("value-map-case").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("delete-block-button").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("HomeBlaze.Components.ValueMap#structuremap") && !text.Contains("Case Value=Stopped"),
            "The case was not deleted.");
    }

    [Fact]
    public async Task WhenThePageIsViewed_ThenNoChipIsShown()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();

        // Act
        await page.SelectEditModeAsync("View", Timeout);

        // Assert
        await Assertions.Expect(page.GetByTestId("invisible-widget-chip")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTheContentOfAResponsiveBlockIsHidden_ThenAChipShowsItAndSelectingItShowsTheContent()
    {
        // Arrange: the browser of the tests is wider than Xs, so the phone card is hidden.
        var page = await OpenPageInInlineModeAsync();
        var chip = page.GetByTestId("responsive-chip");
        await Assertions.Expect(chip).ToContainTextAsync("Xs only", new() { Timeout = Timeout });

        // Act
        await chip.SelectWidgetAsync();

        // Assert
        await Assertions.Expect(page.Locator(".responsive-widget-preview").GetByText("Phone Card")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTheContentOfAResponsiveBlockIsShownSelected_ThenAWidgetInItCanBeSelectedAndTheContentStaysShown()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        await page.GetByTestId("responsive-chip").SelectWidgetAsync();
        var card = page.Locator(".responsive-widget-preview .subject-component-wrapper.selectable").Filter(new() { HasText = "Phone Card" }).First;

        // Act: a click on the title, because the text of the card is a widget of its own.
        await card.GetByText("Phone Card").SelectWidgetAsync();

        // Assert
        await Assertions.Expect(card).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".responsive-widget-preview").GetByText("Phone Card")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenNothingIsSelected_ThenThePageHasInsertLinesAndOneInsertsAWidgetThere()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var lines = page.Locator(".markdown-content > [data-testid='block-insert-line']");
        await Assertions.Expect(lines.First).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act: the second line stands before the switch.
        await lines.Nth(1).ClickAsync();
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByTestId("type-search-input").FillAsync("Alert");
        await dialog.GetByTestId("type-row-alert").ClickAsync();

        // The test id of the name field is on its input.
        await dialog.GetByTestId("create-name-input").FillAsync("structurealert");
        await page.GetByTestId("create-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.IndexOf("HomeBlaze.Components.Alert#structurealert", StringComparison.Ordinal) is var alert and >= 0 &&
                    alert < text.IndexOf("HomeBlaze.Components.Switch", StringComparison.Ordinal),
            "The alert was not inserted before the switch.");
    }

    [Fact]
    public async Task WhenAWidgetIsSelected_ThenOnlyTheContentAroundItHasInsertLines()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();

        // Act
        await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Assert: the switch stands on the page, so the page keeps its lines and no content inside a block has any.
        await Assertions.Expect(page.Locator(".markdown-content > [data-testid='block-insert-line']").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".markdown-fragment [data-testid='block-insert-line']")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAListIsSelected_ThenTheContentOfItsRowsHasInsertLinesOnlyOnceARowIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(Content.Replace(
            "::: Value Path=structuremotor/TargetSpeed Label=\"Row A\"\n:::\n",
            "::: Value Path=structuremotor/TargetSpeed Label=\"Row A\"\n"
            + ":::: HomeBlaze.Components.Progress Path=structuremotor/TargetSpeed Minimum=0 Maximum=1600\n"
            + "::::\n"
            + ":::\n"));
        var row = GetValueRowWrapper(page, "Row A");
        var rowLines = page.Locator(".markdown-fragment [data-testid='block-insert-line']");

        // Act
        await GetValueListWrapper(page).SelectWidgetAsync();

        // Assert: the rows become selectable with the selection of the list, and the content of a row belongs to its row.
        await Assertions.Expect(row).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await Assertions.Expect(rowLines).ToHaveCountAsync(0, new() { Timeout = Timeout });

        // Act
        await row.SelectWidgetAsync();

        // Assert: one line before the progress and one at the end.
        await Assertions.Expect(rowLines).ToHaveCountAsync(2, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAWidgetIsClickedInTheSplitPreview_ThenNothingIsSelectedAndTheEditorSelectsItsBlock()
    {
        // Arrange
        var page = await OpenPageInSplitModeAsync();
        var switchWrapper = page.Locator(".page-split-preview .subject-component-wrapper.revealable:has([data-testid='switch'])").Last;
        await Assertions.Expect(switchWrapper).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await switchWrapper.ClickAsync(new() { Position = new() { X = 2, Y = 2 } });

        // Assert: the editor selects the block only after the round trip of the click, so the selection of the preview
        // would have arrived by then.
        await WaitForEditorSelectionAsync(page, GetLineOf("HomeBlaze.Components.Switch"));
        await Assertions.Expect(page.GetWidgetActionBar()).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".page-split-preview .subject-component-wrapper.selectable")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenTheSwitchIsClickedInTheSplitPreview_ThenItIsNotToggledAndTheEditorSelectsItsBlock()
    {
        // Arrange
        var page = await OpenPageInSplitModeAsync();
        var powerSwitch = page.Locator(".page-split-preview").GetByRole(AriaRole.Switch);
        await Assertions.Expect(powerSwitch).ToBeVisibleAsync(new() { Timeout = Timeout });
        var isCheckedBefore = await powerSwitch.IsCheckedAsync();
        var motor = (Motor)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children["structuremotor"];
        var isPoweredBefore = motor.IsPowered;

        // Act: forced, because the content of the widget takes no pointer input and the wrapper around it takes the click.
        await powerSwitch.ClickAsync(new() { Force = true });

        // Assert: the editor selects the block after the round trip of the click, by which the toggle would have arrived.
        await WaitForEditorSelectionAsync(page, GetLineOf("HomeBlaze.Components.Switch"));
        Assert.Equal(isCheckedBefore, await powerSwitch.IsCheckedAsync());
        Assert.Equal(isPoweredBefore, motor.IsPowered);
    }

    [Fact]
    public async Task WhenASliderInTheSplitPreviewIsOperatedByKeyboard_ThenItWritesNothing()
    {
        // Arrange
        var content = Content.Replace(
            ":: HomeBlaze.Components.Switch Path=structuremotor/IsPowered\n::\n",
            ":: HomeBlaze.Components.Switch Path=structuremotor/IsPowered\n::\n"
            + "\n"
            + ":: HomeBlaze.Components.Slider Path=structuremotor/TargetSpeed Minimum=0 Maximum=3000 Label=\"Preview slider\"\n"
            + "::\n");
        var page = await OpenPageInSplitModeAsync(content);
        var slider = page.Locator(".page-split-preview").GetByRole(AriaRole.Slider, new() { Name = "Preview slider", Exact = true });
        await Assertions.Expect(slider).ToBeVisibleAsync(new() { Timeout = Timeout });
        var motor = (Motor)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children["structuremotor"];
        var targetSpeedBefore = motor.TargetSpeed;

        // Act: the pointer cannot reach the slider, but the keyboard can reach any control that is not disabled.
        await slider.FocusAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        await page.Keyboard.PressAsync("ArrowRight");

        // Assert: the editor selects the block after the round trip of the click, by which a write of the keys would
        // have arrived.
        await page.Locator(".page-split-preview .subject-component-wrapper.revealable:has([data-testid='switch'])").Last
            .ClickAsync(new() { Position = new() { X = 2, Y = 2 } });
        await WaitForEditorSelectionAsync(page, GetLineOf(content, "HomeBlaze.Components.Switch"));
        Assert.Equal(targetSpeedBefore, motor.TargetSpeed);
        Assert.Equal(content, (await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(PageName))).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task WhenAWidgetInTheContentOfARowIsClickedInTheSplitPreview_ThenTheEditorSelectsItsOwnBlock()
    {
        // Arrange: the progress stands inside the list, which takes the clicks on the rest of its content.
        var content = Content.Replace(
            "::: Value Path=structuremotor/TargetSpeed Label=\"Row A\"\n:::\n",
            "::: Value Path=structuremotor/TargetSpeed Label=\"Row A\"\n"
            + ":::: HomeBlaze.Components.Progress Path=structuremotor/TargetSpeed Minimum=0 Maximum=1600\n"
            + "::::\n"
            + ":::\n");
        var page = await OpenPageInSplitModeAsync(content);
        var progress = page.Locator(".page-split-preview .subject-component-wrapper.revealable:has([data-testid='progress'])").Last;
        await Assertions.Expect(progress).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await progress.ClickAsync(new() { Force = true });

        // Assert
        await WaitForEditorSelectionAsync(page, GetLineOf(content, "HomeBlaze.Components.Progress"));
    }

    [Fact]
    public async Task WhenThePageIsSplit_ThenTheSourceEditorShowsBlockNamesInBold()
    {
        // Arrange
        await _fixture.WriteDemoPageAsync(PageName, Content);
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitUntilInteractiveAsync(Timeout);

        // Act
        await page.SelectEditModeAsync("Split", Timeout);

        // Assert
        var name = page.Locator(".monaco-editor .block-name", new() { HasTextString = "HomeBlaze.Components.Switch" });
        await Assertions.Expect(name).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(name).ToHaveCSSAsync("font-weight", "700");
    }

    private async Task<IPage> OpenPageInSplitModeAsync(string content = Content)
    {
        await _fixture.WriteDemoPageAsync(PageName, content);
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitUntilInteractiveAsync(Timeout);
        await page.SelectEditModeAsync("Split", Timeout);

        // The editor only knows its block regions once the decorations are applied, and a click before reveals nothing.
        await Assertions.Expect(page.Locator(".subject-block-decoration").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        return page;
    }

    // The 1-based line of the page that contains the text.
    private static int GetLineOf(string text) => GetLineOf(Content, text);

    private static int GetLineOf(string content, string text) => content.Split('\n').ToList().FindIndex(line => line.Contains(text)) + 1;

    private static Task WaitForEditorSelectionAsync(IPage page, int startLine) =>
        page.WaitForFunctionAsync(
            "line => monaco.editor.getEditors()[0]?.getSelection()?.startLineNumber === line",
            startLine,
            new() { Timeout = Timeout });

    private async Task<IPage> OpenPageInInlineModeAsync(string content = Content)
    {
        await _fixture.WriteDemoPageAsync(PageName, content);
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync(Timeout);
        await page.SwitchToInlineModeAsync();
        return page;
    }

    // A row can be selected once its list is. The list has a bar with a move button as well, so the bar is returned only
    // once it belongs to the row.
    private static async Task<ILocator> SelectRowAsync(IPage page, string rowLabel)
    {
        await GetValueListWrapper(page).SelectWidgetAsync();
        var row = GetValueRowWrapper(page, rowLabel);
        var actionBar = await row.SelectWidgetAsync();
        await Assertions.Expect(row).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        return actionBar;
    }

    private static ILocator GetValueListWrapper(IPage page) =>
        page.Locator(".subject-component-wrapper.selectable:has(> [data-testid='value-list'])");

    private static ILocator GetValueRowWrapper(IPage page, string rowLabel) =>
        page.Locator(".subject-component-wrapper.selectable:has(> [data-testid='value-row'])").Filter(new() { HasText = rowLabel });

    // The innermost widget around the element with the test id.
    private static ILocator GetWrapperOf(IPage page, string testId) =>
        page.Locator($".subject-component-wrapper.selectable:has([data-testid='{testId}'])")
            .Filter(new() { HasNot = page.Locator($".subject-component-wrapper.selectable:has([data-testid='{testId}'])") })
            .First;
}
