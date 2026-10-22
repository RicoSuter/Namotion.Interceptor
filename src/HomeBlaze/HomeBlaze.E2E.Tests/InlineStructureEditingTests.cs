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

    // The first row of the list holds a widget in its content.
    private static readonly string ContentWithProgressInRow = Content.Replace(
        "::: Value Path=structuremotor/TargetSpeed Label=\"Row A\"\n:::\n",
        "::: Value Path=structuremotor/TargetSpeed Label=\"Row A\"\n"
        + ":::: HomeBlaze.Components.Progress Path=structuremotor/TargetSpeed Minimum=0 Maximum=1600\n"
        + "::::\n"
        + ":::\n");

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
    public async Task WhenABlockIsMovedDown_ThenItGoesAfterTheNextBlockInTheFile()
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
    public async Task WhenAWidgetIsMovedDownTwice_ThenItPassesTwoItemsAndStaysSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("move-down-button").ClickAsync();
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => IndexOf(text, "ValueList") < IndexOf(text, "Switch"), "The switch was not moved below the list.");
        await page.GetWidgetActionBar().GetByTestId("move-down-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => IndexOf(text, "ValueMap#structuremap") < IndexOf(text, "Switch") && IndexOf(text, "Switch") < IndexOf(text, "Responsive"),
            "The switch was not moved below the map.");
        await Assertions.Expect(GetWrapperOf(page, "switch")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAListIsMovedDownPastTwoChips_ThenEachMovePassesOneChip()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetValueListWrapper(page).SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("move-down-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => IndexOf(text, "ValueMap#structuremap") < IndexOf(text, "ValueList") && IndexOf(text, "ValueList") < IndexOf(text, "Responsive"),
            "The list did not pass the map alone.");

        // Act
        await page.GetWidgetActionBar().GetByTestId("move-down-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => IndexOf(text, "Responsive") < IndexOf(text, "ValueList"), "The list did not pass the Responsive block.");
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
    public async Task WhenARowIsMovedDown_ThenItStaysSelectedAndItsBarOffersMoveUp()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectRowAsync(page, "Row A");
        await Assertions.Expect(actionBar.GetByTestId("move-up-button")).ToHaveCountAsync(0, new() { Timeout = Timeout });

        // Act
        await actionBar.GetByTestId("move-down-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.IndexOf("Label=\"Row B\"", StringComparison.Ordinal) < text.IndexOf("Label=\"Row A\"", StringComparison.Ordinal),
            "The rows were not swapped.");
        await Assertions.Expect(GetValueRowWrapper(page, "Row A")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        await Assertions.Expect(page.GetWidgetActionBar().GetByTestId("move-up-button")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.GetWidgetActionBar().GetByTestId("move-down-button")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAWidgetOnThePageIsMovedToTheTop_ThenItStaysSelectedAndItsBarNoLongerOffersMoveUp()
    {
        // Arrange: unlike a row of a list, a widget on the page is not rendered again by its parent after the reload.
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();
        await Assertions.Expect(actionBar.GetByTestId("move-up-button")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act: the second move passes the heading, which is the first item of the page.
        await actionBar.GetByTestId("move-up-button").ClickAsync();
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.IndexOf("HomeBlaze.Components.Switch", StringComparison.Ordinal) <
                    text.IndexOf("HomeBlaze.Samples.Motor", StringComparison.Ordinal),
            "The switch was not moved above the motor.");
        await page.GetWidgetActionBar().GetByTestId("move-up-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.IndexOf("HomeBlaze.Components.Switch", StringComparison.Ordinal) <
                    text.IndexOf("# Structure Demo", StringComparison.Ordinal),
            "The switch was not moved above the heading.");
        await Assertions.Expect(GetWrapperOf(page, "switch")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        await Assertions.Expect(page.GetWidgetActionBar().GetByTestId("move-up-button")).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(page.GetWidgetActionBar().GetByTestId("move-down-button")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAWidgetIsInserted_ThenItIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var line = GetInsertLineBefore(GetWrapperOf(page, "switch"));

        // Act
        await OpenWidgetDialogAtAsync(page, line);
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByTestId("type-search-input").FillAsync("Alert");
        await dialog.GetByTestId("type-row-alert").ClickAsync();
        await dialog.GetByTestId("create-name-input").FillAsync("selectedalert");
        await page.GetByTestId("create-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => text.Contains("HomeBlaze.Components.Alert#selectedalert"), "The alert was not inserted.");
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected .mud-alert")).ToHaveCountAsync(1, new() { Timeout = Timeout });
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
        var page = await OpenPageInInlineModeAsync(ContentWithProgressInRow);
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
    public async Task WhenThePageIsEditedInline_ThenTheValueMapChipStandsWhereItsBlockIs()
    {
        // Arrange & Act
        var page = await OpenPageInInlineModeAsync();
        var chip = page.GetByTestId("invisible-widget-chip").Filter(new() { HasText = "structuremap" });
        await Assertions.Expect(chip).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert: the map stands between the list and the Responsive block on the page, and so does its chip.
        var list = await PageGeometry.GetBoxAsync(GetValueListWrapper(page));
        var map = await PageGeometry.GetBoxAsync(chip);
        var responsive = await PageGeometry.GetBoxAsync(page.GetByTestId("responsive-chip"));
        Assert.True(list.Bottom <= map.Top, $"The chip at {map.Top} is not below the list, which ends at {list.Bottom}.");
        Assert.True(map.Bottom <= responsive.Top, $"The chip, which ends at {map.Bottom}, is not above the Responsive chip at {responsive.Top}.");
    }

    [Fact]
    public async Task WhenAValueMapChipIsSelected_ThenItsOutlineFitsTheChip()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var chip = page.GetByTestId("invisible-widget-chip").Filter(new() { HasText = "structuremap" });
        var wrapper = GetWrapperOf(page, "invisible-widget-chip");

        // Act
        await chip.SelectWidgetAsync();
        await Assertions.Expect(wrapper).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });

        // Assert
        var chipBox = await PageGeometry.GetBoxAsync(chip);
        var wrapperBox = await PageGeometry.GetBoxAsync(wrapper);
        Assert.True(wrapperBox.Width <= chipBox.Width + 4, $"The wrapper is {wrapperBox.Width} wide, the chip {chipBox.Width}.");
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
        // A line takes no room, so its button is what shows.
        var lineButtons = page.Locator(".markdown-content > [data-testid='block-insert-line'] button");
        await Assertions.Expect(lineButtons.First).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var line = GetInsertLineBefore(GetWrapperOf(page, "switch"));
        await OpenWidgetDialogAtAsync(page, line);
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
        var page = await OpenPageInInlineModeAsync(ContentWithProgressInRow);

        // Act
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Assert: the switch stands on the page, so the page keeps its lines and no content inside a block has any.
        await Assertions.Expect(page.Locator(".markdown-content > [data-testid='block-insert-line'] button").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await WaitForLinesAfterSelectionAsync(page, actionBar.GetByTestId("edit-subject-button"));
        await Assertions.Expect(page.Locator(".markdown-fragment [data-testid='block-insert-line']")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAListIsSelected_ThenTheContentOfItsRowsHasInsertLinesOnlyOnceARowIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(ContentWithProgressInRow);
        var row = GetValueRowWrapper(page, "Row A");
        var rowLines = page.Locator(".markdown-fragment [data-testid='block-insert-line']");

        // Act
        await GetValueListWrapper(page).SelectWidgetAsync();

        // Assert: the rows become selectable with the selection of the list, and the content of a row belongs to its row.
        await Assertions.Expect(row).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await WaitForLinesAfterSelectionAsync(page, page.GetByTestId("add-value-button"));
        await Assertions.Expect(rowLines).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await page.GetByRole(AriaRole.Dialog).GetByTestId("cancel-button").ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });

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

        // Act: forced, because the content of the preview is inert and the preview itself takes the click.
        await switchWrapper.ClickAsync(new() { Position = new() { X = 2, Y = 2 }, Force = true });

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
        var powerSwitch = page.Locator(".page-split-preview").GetByRole(AriaRole.Switch, new() { IncludeHidden = true });
        await Assertions.Expect(powerSwitch).ToBeVisibleAsync(new() { Timeout = Timeout });
        var isCheckedBefore = await powerSwitch.IsCheckedAsync();
        var motor = (Motor)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children["structuremotor"];
        var isPoweredBefore = motor.IsPowered;

        // Act: forced, because the content of the preview is inert and the preview itself takes the click.
        await powerSwitch.ClickAsync(new() { Force = true });

        // Assert: the editor selects the block after the round trip of the click, by which the toggle would have arrived.
        await WaitForEditorSelectionAsync(page, GetLineOf("HomeBlaze.Components.Switch"));
        Assert.Equal(isCheckedBefore, await powerSwitch.IsCheckedAsync());
        Assert.Equal(isPoweredBefore, motor.IsPowered);
    }

    [Fact]
    public async Task WhenThePageIsSplit_ThenThePreviewIsInertAndItsControlsTakeNoFocus()
    {
        // Arrange: a link ignores IsReadOnly, so only the inert content keeps the focus from it.
        var page = await OpenPageInSplitModeAsync(Content + "\n[Preview link](https://example.com/preview)\n");
        var link = page.Locator(".page-split-preview a[href='https://example.com/preview']");
        await Assertions.Expect(link).ToBeVisibleAsync(new() { Timeout = Timeout });
        var content = page.Locator(".page-split-preview-content");
        await Assertions.Expect(content).ToHaveAttributeAsync("inert", "");

        // Act
        await link.EvaluateAsync("element => element.focus()");

        // Assert
        Assert.False(await link.EvaluateAsync<bool>("element => element === document.activeElement"));

        // Act: without inert the link takes the focus, so the check above does not pass for another reason.
        await content.EvaluateAsync("element => element.inert = false");
        await link.EvaluateAsync("element => element.focus()");

        // Assert
        Assert.True(await link.EvaluateAsync<bool>("element => element === document.activeElement"));
    }

    [Fact]
    public async Task WhenThePointerIsOverAWidgetInTheContentOfARowInTheSplitPreview_ThenOnlyThatWidgetShowsThatAClickRevealsIt()
    {
        // Arrange: the progress stands inside a row of the list, whose wrappers contain the pointer as well.
        var page = await OpenPageInSplitModeAsync(ContentWithProgressInRow);
        var progress = page.Locator(".page-split-preview .subject-component-wrapper.revealable:has([data-testid='progress'])").Last;
        await Assertions.Expect(progress).ToBeVisibleAsync(new() { Timeout = Timeout });
        var hovered = page.Locator(".page-split-preview .subject-component-wrapper.reveal-hover");

        // Act: forced, because the content of the preview is inert.
        await progress.HoverAsync(new() { Force = true });

        // Assert
        await Assertions.Expect(progress).ToHaveClassAsync(new Regex(@"\breveal-hover\b"), new() { Timeout = Timeout });
        await Assertions.Expect(hovered).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".page-split-preview")).ToHaveCSSAsync("cursor", "pointer");

        // Act
        await page.Locator(".monaco-editor").HoverAsync();

        // Assert
        await Assertions.Expect(hovered).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".page-split-preview")).Not.ToHaveCSSAsync("cursor", "pointer");
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
        var slider = page.Locator(".page-split-preview").GetByRole(AriaRole.Slider, new() { Name = "Preview slider", Exact = true, IncludeHidden = true });
        await Assertions.Expect(slider).ToBeVisibleAsync(new() { Timeout = Timeout });
        var motor = (Motor)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children["structuremotor"];
        var targetSpeedBefore = motor.TargetSpeed;

        // Act: the content of the preview is inert, so the focus and the keys must not reach the slider.
        await slider.FocusAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        await page.Keyboard.PressAsync("ArrowRight");

        // Assert: the editor selects the block after the round trip of the click, by which a write of the keys would
        // have arrived.
        await page.Locator(".page-split-preview .subject-component-wrapper.revealable:has([data-testid='switch'])").Last
            .ClickAsync(new() { Position = new() { X = 2, Y = 2 }, Force = true });
        await WaitForEditorSelectionAsync(page, GetLineOf(content, "HomeBlaze.Components.Switch"));
        Assert.Equal(targetSpeedBefore, motor.TargetSpeed);
        Assert.Equal(content, (await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(PageName))).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task WhenAWidgetInTheContentOfARowIsClickedInTheSplitPreview_ThenTheEditorSelectsItsOwnBlock()
    {
        // Arrange: the progress stands inside the list, which takes the clicks on the rest of its content.
        var content = ContentWithProgressInRow;
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

    [Fact]
    public async Task WhenACardIsSelected_ThenNoBlockOfThePageMoves()
    {
        // Arrange: the card has text, a widget and a footer, whose lines show only while the card is selected.
        var content = Content
            + "\n"
            + ":: HomeBlaze.Components.Card Title=\"Layout Card\"\n"
            + "Card text.\n"
            + "\n"
            + "### Card Heading\n"
            + "\n"
            + "::: HomeBlaze.Components.Progress Path=structuremotor/TargetSpeed Minimum=0 Maximum=1600\n"
            + ":::\n"
            + "::: Footer\n"
            + ":::: HomeBlaze.Components.Switch Path=structuremotor/IsPowered\n"
            + "::::\n"
            + ":::\n"
            + "::\n";
        var page = await OpenPageInInlineModeAsync(content);
        var card = page.GetCard("Layout Card");
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = Timeout });
        var before = await GetBlockBoxesAsync(page);

        // Act
        await card.Locator(".mud-card-header").SelectWidgetAsync();
        await Assertions.Expect(card.Locator("[data-testid='block-insert-line'] button").First).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        var after = await GetBlockBoxesAsync(page);
        Assert.Equal(before.Length, after.Length);
        for (var index = 0; index < before.Length; index++)
        {
            Assert.InRange(after[index], before[index] - 0.5, before[index] + 0.5);
        }
    }

    [Fact]
    public async Task WhenInsertLinesAreShown_ThenEachHasANameOfItsOwn()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var buttons = page.Locator("[data-testid='block-insert-line'] button");
        await Assertions.Expect(buttons.First).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var labels = await buttons.EvaluateAllAsync<string[]>("buttons => buttons.map(button => button.getAttribute('aria-label'))");

        // Assert
        Assert.Equal(labels.Length, labels.Distinct().Count());
        Assert.Contains("Insert at the end of the page", labels);
        Assert.Contains("Insert at position 1 of the page", labels);
    }

    [Fact]
    public async Task WhenTheEdgeOfABlockNextToAnInsertLineIsClicked_ThenTheBlockIsSelected()
    {
        // Arrange: nothing is selected, so the page shows its lines, whose bars reach over the edges of the blocks around
        // them. The clicks are at the left, away from the buttons in the middle of the lines.
        var page = await OpenPageInInlineModeAsync();
        await Assertions.Expect(page.Locator(".markdown-content > [data-testid='block-insert-line'] button").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        var switchWrapper = GetWrapperOf(page, "switch");
        var switchBox = (await switchWrapper.BoundingBoxAsync())!;

        // Act
        await page.Mouse.ClickAsync(switchBox.X + 8, switchBox.Y + 2);

        // Assert
        await Assertions.Expect(switchWrapper).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });

        // Act: the list stands right above the line before the value map.
        var listWrapper = GetValueListWrapper(page);
        var listBox = (await listWrapper.BoundingBoxAsync())!;
        await page.Mouse.ClickAsync(listBox.X + 8, listBox.Y + listBox.Height - 2);

        // Assert
        await Assertions.Expect(listWrapper).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Height=180")]
    public async Task WhenAWidgetInACardIsSelected_ThenTheFirstAndTheLastInsertLinesOfTheCardStayInsideIt(string size)
    {
        // Arrange: the content of the card starts and ends with a widget, so its first and its last line stand at its edges.
        // A card with a height scrolls its content.
        var page = await OpenPageInInlineModeAsync(Content + "\n:: HomeBlaze.Components.Card Title=\"Edge Card\"" + size + "\n" + TwoWidgets + "::\n\nText after.\n");
        var card = page.GetCard("Edge Card");

        // Act
        var buttons = await SelectFirstWidgetAndGetInsertButtonsAsync(card);

        // Assert
        var cardBox = (await card.BoundingBoxAsync())!;
        foreach (var button in new[] { buttons.First, buttons.Last })
        {
            var buttonBox = (await button.BoundingBoxAsync())!;
            Assert.InRange(buttonBox.Y, cardBox.Y, cardBox.Y + cardBox.Height - buttonBox.Height);
        }

        var scrollRanges = await GetCardScrollRangesAsync(card);
        Assert.Equal(new[] { 0, 0 }, scrollRanges);
        Assert.True(await IsHitAtItsCenterAsync(buttons.First), "The first button is covered or cut off.");
        Assert.True(await IsHitAtItsCenterAsync(buttons.Last), "The last button is covered or cut off.");
    }

    [Theory]
    [InlineData(":: HomeBlaze.Components.TabLayout\n::: Tab Title=\"First\"\n" + TwoWidgetsInItem + ":::\n::\n", "[data-testid='tab-panel']")]
    [InlineData(":: HomeBlaze.Components.TabLayout Position=Left\n::: Tab Title=\"First\"\n" + TwoWidgetsInItem + ":::\n::\n", "[data-testid='tab-panel']")]
    [InlineData(":: HomeBlaze.Components.FlowLayout MinWidth=200\n::: Item\n" + TwoWidgetsInItem + ":::\n::\n", "[data-testid='flow-item']")]
    public async Task WhenAWidgetInATabOrAFlowItemIsSelected_ThenTheFirstAndTheLastInsertLinesCanBeClickedAndNothingScrollsFurther(string block, string containerSelector)
    {
        // Arrange: the content of the container starts and ends with a widget, so its first and its last line stand at
        // its edges.
        var page = await OpenPageInInlineModeAsync(Content + "\n" + block + "\nText after.\n");
        var container = page.Locator(containerSelector).First;
        await Assertions.Expect(container).ToBeVisibleAsync(new() { Timeout = Timeout });
        var rangesBefore = await GetScrollRangesAsync(container);

        // Act
        var buttons = await SelectFirstWidgetAndGetInsertButtonsAsync(container);

        // Assert
        Assert.True(await IsHitAtItsCenterAsync(buttons.First), "The first button is covered or cut off.");
        Assert.True(await IsHitAtItsCenterAsync(buttons.Last), "The last button is covered or cut off.");

        // The lines of the page hide with the selection, so the page can scroll less, never more.
        var rangesAfter = await GetScrollRangesAsync(container);
        Assert.Equal(rangesBefore.Length, rangesAfter.Length);
        Assert.All(rangesAfter.Zip(rangesBefore), pair => Assert.True(pair.First <= pair.Second, $"A range grew from {pair.Second} to {pair.First}."));
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

    // A content updates its insert lines only after the render of the selection. The dialog that the button opens comes
    // with a later round trip, so the lines are final once it shows, and a test can tell that none are left.
    private static async Task WaitForLinesAfterSelectionAsync(IPage page, ILocator dialogButton)
    {
        await dialogButton.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync(new() { Timeout = Timeout });
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

    private static int IndexOf(string text, string typeName) =>
        text.IndexOf("HomeBlaze.Components." + typeName, StringComparison.Ordinal);

    private static ILocator GetValueListWrapper(IPage page) =>
        page.Locator(".subject-component-wrapper.selectable:has(> [data-testid='value-list'])");

    private static ILocator GetValueRowWrapper(IPage page, string rowLabel) =>
        page.Locator(".subject-component-wrapper.selectable:has(> [data-testid='value-row'])").Filter(new() { HasText = rowLabel });

    // The innermost widget around the element with the test id.
    private static ILocator GetWrapperOf(IPage page, string testId) =>
        page.Locator($".subject-component-wrapper.selectable:has([data-testid='{testId}'])")
            .Filter(new() { HasNot = page.Locator($".subject-component-wrapper.selectable:has([data-testid='{testId}'])") })
            .First;

    // The insert line right before the widget: the lines are siblings of the widgets of their content.
    private static ILocator GetInsertLineBefore(ILocator wrapper) =>
        wrapper.Locator("xpath=preceding-sibling::*[@data-testid='block-insert-line'][1]");

    // Opens the dialog that inserts a widget at the line, through the menu of the line.
    private static async Task OpenWidgetDialogAtAsync(IPage page, ILocator line)
    {
        await line.GetByRole(AriaRole.Button).ClickAsync();
        await page.GetByTestId("insert-widget-item").ClickAsync();
        await Assertions.Expect(page.GetByTestId("create-subject-dialog")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    // The top, left, width and height of every block, paragraph and heading of the page, one after the other.
    private static Task<double[]> GetBlockBoxesAsync(IPage page) =>
        page.Locator(".markdown-content").EvaluateAsync<double[]>(
            """
            content => [...content.querySelectorAll('.subject-component-wrapper, p, h3')].flatMap(element => {
                const box = element.getBoundingClientRect();
                return [box.top, box.left, box.width, box.height];
            })
            """);

    // Two widgets, so that the content they are in starts and ends with a widget.
    private const string TwoWidgets =
        "::: HomeBlaze.Components.Switch Path=structuremotor/IsPowered\n:::\n"
        + "::: HomeBlaze.Components.Progress Path=structuremotor/TargetSpeed Minimum=0 Maximum=1600\n:::\n";

    private const string TwoWidgetsInItem =
        ":::: HomeBlaze.Components.Switch Path=structuremotor/IsPowered\n::::\n"
        + ":::: HomeBlaze.Components.Progress Path=structuremotor/TargetSpeed Minimum=0 Maximum=1600\n::::\n";

    // Selects the first widget in the container, which shows the insert lines of the content it is in: one before each of
    // the two widgets and one at the end.
    private static async Task<ILocator> SelectFirstWidgetAndGetInsertButtonsAsync(ILocator container)
    {
        await container.Locator(".subject-component-wrapper.selectable").First.SelectWidgetAsync();
        var buttons = container.Locator("[data-testid='block-insert-line'] button");
        await Assertions.Expect(buttons).ToHaveCountAsync(3, new() { Timeout = Timeout });
        return buttons;
    }

    // Whether a click at the center of the element reaches it, which it does not when it is covered or cut off.
    private static Task<bool> IsHitAtItsCenterAsync(ILocator element) =>
        element.EvaluateAsync<bool>(
            """
            element => {
                element.scrollIntoView({ block: 'center' });
                const box = element.getBoundingClientRect();
                return element.contains(document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2));
            }
            """);

    // How far the card and its content can scroll down.
    private static Task<int[]> GetCardScrollRangesAsync(ILocator card) =>
        card.EvaluateAsync<int[]>("card => [card, card.querySelector('.mud-card-content')].map(element => element.scrollHeight - element.clientHeight)");

    // How far each element that scrolls can scroll, down and sideways: the element, the elements in it and around it, and
    // the page.
    private static Task<int[]> GetScrollRangesAsync(ILocator element) =>
        element.EvaluateAsync<int[]>(
            """
            element => {
                const ancestors = [];
                for (let current = element.parentElement; current; current = current.parentElement) {
                    ancestors.push(current);
                }

                return [element, ...element.querySelectorAll('*'), ...ancestors]
                    .filter(current => current === document.scrollingElement || [getComputedStyle(current).overflowX, getComputedStyle(current).overflowY].some(overflow => overflow === 'auto' || overflow === 'scroll'))
                    .flatMap(current => [current.scrollHeight - current.clientHeight, current.scrollWidth - current.clientWidth]);
            }
            """);
}
