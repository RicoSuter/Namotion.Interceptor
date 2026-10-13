using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for widget blocks and slots on markdown pages.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class MarkdownBlockTests
{
    private static readonly Regex TemperaturePattern = new(@"temperature of -?\d+([.,]\d+)?°C");

    private const int NarrowViewportWidth = 390;

    // The page that tests overwrite. Each of them writes its own content before it opens the page.
    private const string ScratchPage = "Scratch.md";

    private readonly PlaywrightFixture _fixture;

    public MarkdownBlockTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenPageAsync(string name)
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{name}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        return page;
    }

    private static ILocator GetCard(IPage page, string title)
    {
        return page.GetByTestId("card").Filter(new() { HasText = title });
    }

    [Fact]
    public async Task WhenPageHasCard_ThenTitleContentAndFooterAreInsideIt()
    {
        // Arrange & Act
        var page = await OpenPageAsync("Blocks.md");

        // Assert
        var card = page.GetByTestId("card").First;
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(card).ToContainTextAsync("Motor Card");
        await Assertions.Expect(card).ToContainTextAsync("Card body with a live value");
        await Assertions.Expect(card).ToContainTextAsync("Card footer text");
    }

    [Fact]
    public async Task WhenCardContainsWidgetAndExpression_ThenBothRenderLiveInsideTheCard()
    {
        // Arrange & Act
        var page = await OpenPageAsync("Blocks.md");

        // Assert
        var card = page.GetByTestId("card").First;
        await Assertions.Expect(card).ToContainTextAsync("CardMotor", new() { Timeout = 30000 });
        await Assertions.Expect(card).ToContainTextAsync(new Regex(@"speed is [1-9]\d* RPM"), new() { Timeout = 30000 });
    }

    [Fact]
    public async Task WhenPageRenders_ThenItShowsNoBlockErrors()
    {
        // Arrange & Act
        var page = await OpenPageAsync("Blocks.md");

        // Assert
        await Assertions.Expect(page.GetByTestId("card").First).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenWidgetsInACardReferToSubjectsByNameAndByRelativePath_ThenBothRenderTheReferencedSubject()
    {
        // Arrange & Act
        var page = await OpenPageAsync("Blocks.md");

        // Assert
        var card = GetCard(page, "Reference Card");
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(card).ToContainTextAsync("CardMotor", new() { Timeout = 30000 });
        await Assertions.Expect(card).ToContainTextAsync("Test Motor", new() { Timeout = 30000 });
        await Assertions.Expect(card).Not.ToContainTextAsync("Cannot resolve path");
    }

    [Fact]
    public async Task WhenExpressionInACardUsesANamedSubject_ThenItsValueChangesOverTime()
    {
        // Arrange
        var page = await OpenPageAsync("Blocks.md");
        var card = GetCard(page, "Reference Card");
        await Assertions.Expect(card).ToContainTextAsync(TemperaturePattern, new() { Timeout = 30000 });

        // Act
        var firstValue = TemperaturePattern.Match(await card.InnerTextAsync()).Value;

        // Assert: the temperature of the motor has noise, so every simulation step gives a new value.
        Assert.NotEmpty(firstValue);
        await Assertions.Expect(card).Not.ToContainTextAsync(firstValue, new() { Timeout = 30000 });
        await Assertions.Expect(card).ToContainTextAsync(TemperaturePattern);
    }

    [Fact]
    public async Task WhenWidgetsReferToTheCardsThatContainThem_ThenAMessageEndsTheRendering()
    {
        // Arrange & Act
        var page = await OpenPageAsync("Cycles.md");

        // Assert
        await Assertions.Expect(page.GetByText("Text after the cards.")).ToBeVisibleAsync(new() { Timeout = 30000 });

        var selfCard = GetCard(page, "Self Card");
        await Assertions.Expect(selfCard).ToHaveCountAsync(1);
        await Assertions.Expect(selfCard.GetByTestId("widget-cycle")).ToHaveCountAsync(1);

        // Each of the two cards shows the other, which shows the first again, and there the reference
        // finds itself further out and stops: three titles of each card and one message per chain.
        await Assertions.Expect(page.GetByText("First Card", new() { Exact = true })).ToHaveCountAsync(3);
        await Assertions.Expect(page.GetByText("Second Card", new() { Exact = true })).ToHaveCountAsync(3);
        await Assertions.Expect(page.GetByTestId("widget-cycle")).ToHaveCountAsync(3);

        var firstCard = GetCard(page, "First Card").First;
        await Assertions.Expect(firstCard.GetByTestId("widget-cycle")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task WhenTwoCardsFollowEachOther_ThenTheyAreSeparatedLikeParagraphs()
    {
        // Arrange
        var page = await OpenPageAsync("Blocks.md");
        var firstCard = GetCard(page, "Motor Card");
        var secondCard = GetCard(page, "Reference Card");
        await Assertions.Expect(secondCard).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Act: both cards are measured in one call, because the live motor in the first one changes its height.
        var gap = await secondCard.EvaluateAsync<double>(
            "(second, first) => second.getBoundingClientRect().top - first.getBoundingClientRect().bottom",
            await firstCard.ElementHandleAsync());

        // Assert: 0.75rem, the margin of a paragraph of the page.
        Assert.Equal(12.0, gap, 1.0);
    }

    [Fact]
    public async Task WhenPageHasFlowLayout_ThenEachItemBlockIsOneCell()
    {
        // Arrange & Act
        var page = await OpenPageAsync("Blocks.md");

        // Assert
        var items = page.GetByTestId("flow-item");
        await Assertions.Expect(items).ToHaveCountAsync(3, new() { Timeout = 30000 });
        await Assertions.Expect(items.Nth(0)).ToContainTextAsync("Flow One");
        await Assertions.Expect(items.Nth(1)).ToContainTextAsync("Second cell");
        await Assertions.Expect(items.Nth(2)).ToContainTextAsync("Plain text cell");
    }

    [Fact]
    public async Task WhenViewportIsWide_ThenFlowItemsAreSideBySide()
    {
        // Arrange
        var page = await OpenPageAsync("Blocks.md");
        var items = page.GetByTestId("flow-item");
        await Assertions.Expect(items).ToHaveCountAsync(3, new() { Timeout = 30000 });

        // Act
        await page.SetViewportSizeAsync(1280, 800);

        // Assert
        await WaitForLayoutAsync(page, "flow-layout", width => width > 3 * 240);
        var boxes = await GetBoxesAsync(items);
        Assert.Equal(boxes[0].Top, boxes[1].Top, 1.0);
        Assert.Equal(boxes[1].Top, boxes[2].Top, 1.0);
        Assert.True(boxes[1].Left >= boxes[0].Right);
        Assert.True(boxes[2].Left >= boxes[1].Right);
        Assert.Equal(boxes[0].Right - boxes[0].Left, boxes[2].Right - boxes[2].Left, 1.0);
    }

    [Fact]
    public async Task WhenViewportIsNarrow_ThenFlowItemsStackInOneColumn()
    {
        // Arrange
        var page = await OpenPageAsync("Blocks.md");
        var items = page.GetByTestId("flow-item");
        await Assertions.Expect(items).ToHaveCountAsync(3, new() { Timeout = 30000 });

        // Act
        await page.SetViewportSizeAsync(NarrowViewportWidth, 844);

        // Assert
        await Assertions.Expect(items.Nth(1)).ToBeVisibleAsync();
        await WaitForLayoutAsync(page, "flow-layout", IsNarrowContentWidth);
        var boxes = await GetBoxesAsync(items);
        Assert.Equal(boxes[0].Left, boxes[1].Left, 1.0);
        Assert.Equal(boxes[1].Left, boxes[2].Left, 1.0);
        Assert.True(boxes[1].Top >= boxes[0].Bottom);
        Assert.True(boxes[2].Top >= boxes[1].Bottom);
        Assert.All(boxes, box => Assert.InRange(box.Right, 0, NarrowViewportWidth));
        Assert.False(await HasHorizontalScrollingAsync(page));
    }

    [Fact]
    public async Task WhenViewportIsNarrowAndAFlowItemHoldsAWidgetWithAMinimumWidth_ThenThePageDoesNotScrollSideways()
    {
        // Arrange
        var page = await OpenPageAsync("Layouts.md");
        var items = page.GetByTestId("flow-item");
        await Assertions.Expect(items).ToHaveCountAsync(2, new() { Timeout = 30000 });
        await Assertions.Expect(items.Nth(0)).ToContainTextAsync("FlowMotor", new() { Timeout = 30000 });

        // Act
        await page.SetViewportSizeAsync(NarrowViewportWidth, 844);

        // Assert
        await WaitForLayoutAsync(page, "flow-layout", IsNarrowContentWidth);
        var boxes = await GetBoxesAsync(items);
        var motor = (await GetBoxesAsync(items.Nth(0).Locator(".mud-paper"))).Single();
        Assert.Equal(boxes[0].Left, boxes[1].Left, 1.0);
        Assert.True(boxes[1].Top >= boxes[0].Bottom);
        Assert.All(boxes, box => Assert.InRange(box.Right, 0, NarrowViewportWidth));
        Assert.InRange(motor.Left, boxes[0].Left, boxes[0].Right);
        Assert.InRange(motor.Right, boxes[0].Left, boxes[0].Right);
        Assert.False(await HasHorizontalScrollingAsync(page));
    }

    [Fact]
    public async Task WhenACardIsTheOnlyContentOfAFlowItem_ThenTheItemIsAsHighAsTheCard()
    {
        // Arrange
        var page = await OpenPageAsync("Blocks.md");
        var item = page.GetByTestId("flow-item").Nth(0);
        await Assertions.Expect(item.GetByTestId("card")).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Act
        var itemHeight = await GetContentHeightAsync(item);
        var card = (await GetBoxesAsync(item.GetByTestId("card"))).Single();

        // Assert: the margin that separates cards on a page would make the item higher than its card.
        Assert.Equal(card.Bottom - card.Top, itemHeight, 1.0);
    }

    [Fact]
    public async Task WhenACardIsTheOnlyContentOfATab_ThenThePanelAddsOnlyItsPadding()
    {
        // Arrange
        var page = await OpenPageAsync("Layouts.md");
        var panel = page.GetByTestId("tab-panel").First;
        await Assertions.Expect(panel.GetByTestId("card")).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Act
        var panelHeight = await GetContentHeightAsync(panel);
        var card = (await GetBoxesAsync(panel.GetByTestId("card"))).Single();

        // Assert
        Assert.Equal(card.Bottom - card.Top, panelHeight, 1.0);
    }

    [Fact]
    public async Task WhenPageHasTabLayout_ThenEachTabBlockIsOneTabWithItsTitleAndIcon()
    {
        // Arrange & Act
        var page = await OpenPageAsync("Blocks.md");

        // Assert
        var tabs = page.GetByTestId("tab-layout").GetByRole(AriaRole.Tab);
        await Assertions.Expect(tabs).ToHaveTextAsync(new[] { "First Tab", "Second Tab" }, new() { Timeout = 30000 });
        await Assertions.Expect(tabs.Nth(0).Locator("svg")).ToHaveCountAsync(1);
        await Assertions.Expect(tabs.Nth(1).Locator("svg")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenSecondTabIsClicked_ThenItsContentIsShown()
    {
        // Arrange
        var page = await OpenPageAsync("Blocks.md");
        var tabs = page.GetByTestId("tab-layout");
        await Assertions.Expect(tabs.GetByText("Content of the first tab")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(tabs.GetByText("Content of the second tab")).ToBeHiddenAsync();

        // Act
        await GetTab(tabs, "Second Tab").ClickAsync();

        // Assert
        await Assertions.Expect(tabs.GetByText("Content of the second tab")).ToBeVisibleAsync();
        await Assertions.Expect(tabs.GetByText("Content of the first tab")).ToBeHiddenAsync();
        await Assertions.Expect(GetSelectedTab(tabs)).ToHaveTextAsync("Second Tab");
    }

    [Fact]
    public async Task WhenATabThatWasOpenedByAClickShowsASubjectOutsideThePage_ThenItsValuesChangeOverTime()
    {
        // Arrange: nothing on the page changes by itself, only the motor next to it does.
        await WriteScratchPageAsync(CreateTabsPage(
            string.Empty,
            CreateTab("text", "Text", "Static text"),
            CreateTab("motor", "Motor", ":::: HomeBlaze.Components.Widget Path=../Motor\n::::")));

        var page = await OpenPageAsync(ScratchPage);
        var tabs = page.GetByTestId("tab-layout");
        await Assertions.Expect(tabs.GetByText("Static text")).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Act
        await GetTab(tabs, "Motor").ClickAsync();

        // Assert: the temperature of the motor has noise, so every simulation step gives a new value.
        var panel = tabs.GetByTestId("tab-panel").Nth(1);
        await Assertions.Expect(panel).ToContainTextAsync("Test Motor", new() { Timeout = 30000 });
        var firstText = await panel.InnerTextAsync();
        await Assertions.Expect(panel).Not.ToHaveTextAsync(firstText, new() { Timeout = 30000, UseInnerText = true });
    }

    [Fact]
    public async Task WhenAReloadInsertsATabAndABlockBeforeTheSelectedTab_ThenTheSelectedTabStaysSelected()
    {
        // Arrange
        var page = await OpenScratchPageWithSecondOfThreeTabsSelectedAsync();
        var tabs = page.GetByTestId("tab-layout");

        // Act
        await WriteScratchPageAsync(CreateTabsPage(
            ":: HomeBlaze.Components.Card Title=\"Inserted Card\"\nInserted\n::\n\n",
            CreateTab("first", "First"),
            CreateTab("alpha", "Alpha"),
            CreateTab("beta", "Beta"),
            CreateTab("gamma", "Gamma")));

        // Assert
        await Assertions.Expect(GetCard(page, "Inserted Card")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(tabs.GetByRole(AriaRole.Tab)).ToHaveTextAsync(new[] { "First", "Alpha", "Beta", "Gamma" });
        await Assertions.Expect(GetSelectedTab(tabs)).ToHaveTextAsync("Beta");
        await Assertions.Expect(tabs.GetByText("Content of beta")).ToBeVisibleAsync();
        await Assertions.Expect(tabs.GetByText("Content of first")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task WhenAReloadReordersAndRenamesTabs_ThenTheTabBarFollowsThePageAndTheSelectedTabStaysSelected()
    {
        // Arrange
        var page = await OpenScratchPageWithSecondOfThreeTabsSelectedAsync();
        var tabs = page.GetByTestId("tab-layout");

        // Act
        await WriteScratchPageAsync(CreateTabsPage(
            string.Empty,
            CreateTab("gamma", "Gamma"),
            CreateTab("alpha", "Alpha"),
            CreateTab("beta", "Beta Renamed")));

        // Assert
        await Assertions.Expect(tabs.GetByRole(AriaRole.Tab)).ToHaveTextAsync(
            new[] { "Gamma", "Alpha", "Beta Renamed" }, new() { Timeout = 30000 });
        await Assertions.Expect(GetSelectedTab(tabs)).ToHaveTextAsync("Beta Renamed");
        await Assertions.Expect(tabs.GetByText("Content of beta")).ToBeVisibleAsync();
        await Assertions.Expect(tabs.GetByText("Content of gamma")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task WhenAReloadRemovesTheSelectedTab_ThenTheFirstTabIsShown()
    {
        // Arrange
        var page = await OpenScratchPageWithSecondOfThreeTabsSelectedAsync();
        var tabs = page.GetByTestId("tab-layout");

        // Act
        await WriteScratchPageAsync(CreateTabsPage(
            string.Empty,
            CreateTab("alpha", "Alpha"),
            CreateTab("gamma", "Gamma")));

        // Assert
        await Assertions.Expect(tabs.GetByRole(AriaRole.Tab)).ToHaveTextAsync(
            new[] { "Alpha", "Gamma" }, new() { Timeout = 30000 });
        await Assertions.Expect(GetSelectedTab(tabs)).ToHaveTextAsync("Alpha");
        await Assertions.Expect(tabs.GetByText("Content of alpha")).ToBeVisibleAsync();
        await Assertions.Expect(tabs.GetByText("Content of gamma")).ToBeHiddenAsync();
        await Assertions.Expect(tabs.GetByText("Content of beta")).ToHaveCountAsync(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Top")]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Bottom")]
    public async Task WhenTabLayoutHasAPosition_ThenTheTabBarIsOnThatSideOfTheContent(string? position)
    {
        // Arrange
        await WriteScratchPageAsync(CreatePositionedTabsPage(position));
        var page = await OpenPageAsync(ScratchPage);
        await page.SetViewportSizeAsync(1280, 800);
        var tabs = page.GetByTestId("tab-layout");
        await Assertions.Expect(tabs.GetByText("Content of alpha")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await WaitForLayoutAsync(page, "tab-layout", width => width > 800);

        // Act
        var layout = (await GetBoxesAsync(tabs)).Single();
        var bar = (await GetBoxesAsync(tabs.Locator(".mud-tabs-tabbar"))).Single();
        var panel = (await GetBoxesAsync(tabs.GetByTestId("tab-panel").First)).Single();
        var buttons = await GetBoxesAsync(tabs.GetByRole(AriaRole.Tab));

        // Assert: the tabs stand below each other at a side and next to each other above or below the content.
        var isAtASide = position is "Left" or "Right";
        Assert.True(isAtASide ? buttons[1].Top >= buttons[0].Bottom : buttons[1].Left >= buttons[0].Right, $"{buttons[0]} {buttons[1]}");
        Assert.True(position switch
        {
            "Left" => bar.Right <= panel.Left,
            "Right" => bar.Left >= panel.Right,
            "Bottom" => bar.Top >= panel.Bottom,
            _ => bar.Bottom <= panel.Top
        }, $"bar {bar}, panel {panel}");

        // The content takes the room that the tab bar leaves, and no more.
        var panelWidth = panel.Right - panel.Left;
        var expectedWidth = layout.Right - layout.Left - (isAtASide ? bar.Right - bar.Left : 0);
        Assert.Equal(expectedWidth, panelWidth, 1.0);
        Assert.False(await HasHorizontalScrollingAsync(page));

        // The same holds for a tab with content that is narrower than that room.
        await GetTab(tabs, "Beta").ClickAsync();
        await Assertions.Expect(tabs.GetByText("Content of beta")).ToBeVisibleAsync();
        var shortPanel = (await GetBoxesAsync(tabs.GetByTestId("tab-panel").Nth(1))).Single();
        Assert.Equal(expectedWidth, shortPanel.Right - shortPanel.Left, 1.0);
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    public async Task WhenTabsAreAtASideAndTheViewportGetsNarrow_ThenTheTabBarMovesToTheTopAndBackWhenItIsWideAgain(string position)
    {
        // Arrange
        await WriteScratchPageAsync(CreatePositionedTabsPage(position));
        var page = await OpenPageAsync(ScratchPage);
        await page.SetViewportSizeAsync(1280, 800);
        var tabs = page.GetByTestId("tab-layout");
        await Assertions.Expect(tabs.GetByText("Content of alpha")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await WaitForTabBarAsync(page, beside: true);
        await GetTab(tabs, "Beta").ClickAsync();
        await Assertions.Expect(tabs.GetByText("Content of beta")).ToBeVisibleAsync();

        // Act & Assert: the selected tab stays selected through both changes.
        await page.SetViewportSizeAsync(NarrowViewportWidth, 844);
        await WaitForTabBarAsync(page, beside: false);
        await Assertions.Expect(GetSelectedTab(tabs)).ToHaveTextAsync("Beta");
        Assert.False(await HasHorizontalScrollingAsync(page));

        await page.SetViewportSizeAsync(1280, 800);
        await WaitForTabBarAsync(page, beside: true);
        await Assertions.Expect(GetSelectedTab(tabs)).ToHaveTextAsync("Beta");
        await Assertions.Expect(tabs.GetByText("Content of beta")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WhenAWidgetInACardIsEditedInInlineMode_ThenOnlyItsHeadLineChangesInThePageSource()
    {
        // Arrange: a copy of the blocks page, written fresh so that the test does not depend on an earlier run.
        var blocksPage = await File.ReadAllTextAsync(GetPagePath("Blocks.md"));
        await WriteScratchPageAsync(blocksPage.Replace("title: Blocks Demo", "title: Scratch"));
        var sourceBefore = await File.ReadAllLinesAsync(GetPagePath(ScratchPage));

        var page = await OpenPageAsync(ScratchPage);
        var card = GetCard(page, "Motor Card");
        await Assertions.Expect(card).ToContainTextAsync("/ 1200 RPM", new() { Timeout = 30000 });

        // Act
        await SelectEditModeAsync(page, "Inline");
        await card.GetByTestId("edit-subject-button").ClickAsync();

        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByLabel("Target Speed (RPM)").FillAsync("1500");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await SelectEditModeAsync(page, "View");

        // Assert
        await Assertions.Expect(card).ToContainTextAsync("/ 1500 RPM", new() { Timeout = 30000 });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);

        var sourceAfter = await File.ReadAllLinesAsync(GetPagePath(ScratchPage));
        Assert.Equal(sourceBefore.Length, sourceAfter.Length);

        var changedLines = Enumerable.Range(0, sourceBefore.Length)
            .Where(index => sourceBefore[index] != sourceAfter[index])
            .ToArray();

        var changedLine = Assert.Single(changedLines);
        Assert.Equal(
            "::: HomeBlaze.Samples.Motor#cardmotor Name=CardMotor TargetSpeed=1200 SimulationInterval=00:00:01",
            sourceBefore[changedLine]);
        Assert.Equal(
            "::: HomeBlaze.Samples.Motor#cardmotor Name=CardMotor TargetSpeed=1500 SimulationInterval=00:00:01",
            sourceAfter[changedLine]);
    }

    [Fact]
    public async Task WhenAWidgetInACardOfAPageWithoutClosingLinesIsEditedInTheEditor_ThenOnlyItsHeadLineChangesAndNoClosingLineIsAdded()
    {
        // Arrange: a layout in the short form, in which a head line ends the blocks before it.
        const string source =
            "---\ntitle: Scratch\n---\n\n# Scratch\n\n" +
            ":: HomeBlaze.Components.FlowLayout MinWidth=320\n" +
            "::: Item\n" +
            ":::: HomeBlaze.Components.Card Title=\"Short Card\"\n" +
            "The motor of the short card.\n" +
            "::::: HomeBlaze.Samples.Motor#shortmotor Name=ShortMotor TargetSpeed=1200 SimulationInterval=00:00:01\n" +
            "::: Item\n" +
            ":::: HomeBlaze.Components.Card Title=\"Other Card\"\n" +
            "The text of the other card.\n";
        await WriteScratchPageAsync(source);

        var page = await OpenPageAsync(ScratchPage);
        var card = GetCard(page, "Short Card");
        await Assertions.Expect(card).ToContainTextAsync("/ 1200 RPM", new() { Timeout = 30000 });
        await Assertions.Expect(GetCard(page, "Other Card")).ToContainTextAsync("The text of the other card.");
        await SelectEditModeAsync(page, "Split");

        // The editor only knows its block regions once the decorations are applied.
        await Assertions.Expect(page.Locator(".subject-block-decoration").First).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Act
        var headLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "HomeBlaze.Samples.Motor#shortmotor" });
        await Assertions.Expect(headLine).ToBeVisibleAsync(new() { Timeout = 30000 });
        await headLine.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit shortmotor" }).ClickAsync();

        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByLabel("Target Speed (RPM)").FillAsync("1500");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        // Assert: the editor has the new value, which it shows only after it wrote the page
        var writtenHeadLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "TargetSpeed=1500" });
        await Assertions.Expect(writtenHeadLine).ToBeVisibleAsync(new() { Timeout = 30000 });
        await SelectEditModeAsync(page, "View");
        await Assertions.Expect(card).ToContainTextAsync("/ 1500 RPM", new() { Timeout = 30000 });
        await Assertions.Expect(GetCard(page, "Other Card")).ToContainTextAsync("The text of the other card.");
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);

        var written = (await File.ReadAllTextAsync(GetPagePath(ScratchPage))).Replace("\r\n", "\n");
        Assert.Equal(source.Replace("TargetSpeed=1200", "TargetSpeed=1500"), written);
    }

    [Fact]
    public async Task WhenNameInTheCreateDialogIsNoValidBlockName_ThenCreateStaysDisabledUntilTheNameIsCorrected()
    {
        // Arrange
        await WriteScratchPageAsync("# Scratch\n\nText before the block\n");

        var page = await OpenPageAsync(ScratchPage);
        await Assertions.Expect(page.GetByText("Text before the block")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await SelectEditModeAsync(page, "Split");

        var textLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "Text before the block" });
        await Assertions.Expect(textLine).ToBeVisibleAsync(new() { Timeout = 30000 });
        await textLine.ClickAsync();

        await page.GetByTestId("create-subject-button").ClickAsync();
        await Assertions.Expect(SubjectSetupDialogTests.TypeRows(page).First).ToBeVisibleAsync(new() { Timeout = 30000 });
        await SubjectSetupDialogTests.PickTypeAsync(page, "motor");

        var dialog = SubjectSetupDialogTests.Dialog(page);
        var blockNameInput = dialog.GetByLabel("Block name");
        var createButton = SubjectSetupDialogTests.CreateButton(page);

        // The dialog suggests the name of the type and does not show the name as that of a file. The name
        // of a motor is required, so Create is enabled only once it is entered, and from then on only the
        // block name can disable it.
        await Assertions.Expect(blockNameInput).ToHaveValueAsync("Motor", new() { Timeout = 30000 });
        await Assertions.Expect(dialog.GetByText(".json", new() { Exact = true })).ToHaveCountAsync(0);
        await SubjectSetupDialogTests.EnterMotorNameAsync(page, "Living Room Motor");
        await Assertions.Expect(createButton).ToBeEnabledAsync(new() { Timeout = 30000 });

        // Act: a name that is fine for a file, but not for a block
        await blockNameInput.FillAsync("Living Room");

        // Assert: the dialog says what a valid name is and does not create
        await Assertions.Expect(dialog.GetByText("'Living Room' cannot be used as a name on a page. A name starts with a letter"))
            .ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(createButton).ToBeDisabledAsync();

        // Act: correct the name and finish
        await blockNameInput.FillAsync("livingroom");
        await Assertions.Expect(createButton).ToBeEnabledAsync(new() { Timeout = 30000 });
        await Assertions.Expect(dialog).Not.ToContainTextAsync("cannot be used as a name on a page");
        await createButton.ClickAsync();

        // Assert: the block is in the editor, in the file and on the page
        await Assertions.Expect(dialog).ToBeHiddenAsync(new() { Timeout = 30000 });
        var headLine = page.Locator(
            ".monaco-editor .view-line", new() { HasTextString = "HomeBlaze.Samples.Motor#livingroom" });
        await Assertions.Expect(headLine).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);

        var source = await File.ReadAllTextAsync(GetPagePath(ScratchPage));
        Assert.Contains("\n:: HomeBlaze.Samples.Motor#livingroom Name=\"Living Room Motor\"", source);
        Assert.Contains("Text before the block", source);
    }

    [Fact]
    public async Task WhenABlockIsCreatedWithTheCursorInTheJsonBlockOfACard_ThenItGoesAfterTheJsonBlockWithOneMoreFenceCharacter()
    {
        // Arrange: the title of the card is in its json block, so the card loses it if the json block is damaged.
        const string cardStart = ":: HomeBlaze.Components.Card\n```json\n{ \"Title\": \"Outer Card\" }\n```\n";
        await WriteScratchPageAsync($"# Scratch\n\n{cardStart}Text inside the card\n::\n\nText after the card\n");

        var page = await OpenPageAsync(ScratchPage);
        var card = GetCard(page, "Outer Card");
        await Assertions.Expect(card.GetByText("Text inside the card")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await SelectEditModeAsync(page, "Split");

        var jsonLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "\"Title\": \"Outer Card\"" });
        await Assertions.Expect(jsonLine).ToBeVisibleAsync(new() { Timeout = 30000 });
        await jsonLine.ClickAsync();

        // Act
        await page.GetByTestId("create-subject-button").ClickAsync();
        await Assertions.Expect(SubjectSetupDialogTests.TypeRows(page).First).ToBeVisibleAsync(new() { Timeout = 30000 });
        await SubjectSetupDialogTests.PickTypeAsync(page, "motor");

        var dialog = SubjectSetupDialogTests.Dialog(page);
        var createButton = SubjectSetupDialogTests.CreateButton(page);
        await dialog.GetByLabel("Block name").FillAsync("innermotor");
        await SubjectSetupDialogTests.EnterMotorNameAsync(page, "Inner Motor");
        await Assertions.Expect(createButton).ToBeEnabledAsync(new() { Timeout = 30000 });
        await createButton.ClickAsync();

        // Assert: the block is in the file after the json block with the fence of a block inside the card,
        // and the card shows it and still has its title
        await Assertions.Expect(dialog).ToBeHiddenAsync(new() { Timeout = 30000 });
        await SelectEditModeAsync(page, "View");
        await Assertions.Expect(card).ToContainTextAsync("Inner Motor", new() { Timeout = 30000 });
        await Assertions.Expect(card.GetByText("Text inside the card")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);

        var source = (await File.ReadAllTextAsync(GetPagePath(ScratchPage))).Replace("\r\n", "\n");
        var lines = source.Split('\n');
        var headLine = Array.FindIndex(lines, line => line.Contains("#innermotor", StringComparison.Ordinal));
        Assert.StartsWith("::: HomeBlaze.Samples.Motor#innermotor Name=\"Inner Motor\"", lines[headLine]);
        Assert.Equal(":::", lines[headLine + 1]);
        Assert.Contains(cardStart, source);
        Assert.True(headLine > Array.IndexOf(lines, "```"));
        Assert.True(headLine < Array.IndexOf(lines, "Text inside the card"));
        Assert.Contains("Text inside the card\n::\n\nText after the card", source);
    }

    [Fact]
    public async Task WhenValueTileIsCreatedInTheEditor_ThenItsBlockIsWrittenToThePage()
    {
        // Arrange
        await WriteScratchPageAsync("# Scratch\n\nText before the block\n");

        var page = await OpenPageAsync(ScratchPage);
        await Assertions.Expect(page.GetByText("Text before the block")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await SelectEditModeAsync(page, "Split");

        var textLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "Text before the block" });
        await Assertions.Expect(textLine).ToBeVisibleAsync(new() { Timeout = 30000 });
        await textLine.ClickAsync();

        await page.GetByTestId("create-subject-button").ClickAsync();
        await Assertions.Expect(SubjectSetupDialogTests.TypeRows(page).First).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Act: the tile is found in the group "Dashboard" under its display name
        await page.GetByTestId("type-group-dashboard").ClickAsync();
        var tileRow = SubjectSetupDialogTests.TypeRows(page).Filter(new() { HasText = "Value tile" });
        await Assertions.Expect(tileRow).ToHaveCountAsync(1);
        await tileRow.ClickAsync();

        var dialog = SubjectSetupDialogTests.Dialog(page);
        var createButton = SubjectSetupDialogTests.CreateButton(page);
        await Assertions.Expect(createButton).ToBeVisibleAsync(new() { Timeout = 30000 });
        await dialog.GetByLabel("Path", new() { Exact = true }).FillAsync("scratchmotor/CurrentSpeed");
        await Assertions.Expect(createButton).ToBeEnabledAsync(new() { Timeout = 30000 });
        await createButton.ClickAsync();

        // Assert: the block is in the editor and in the file, with its head line and a closing line
        await Assertions.Expect(dialog).ToBeHiddenAsync(new() { Timeout = 30000 });
        // The text of the editor and not a line of its view: the editor wraps a long head line, also after a
        // dot, and each part is then a line of the view. Where it wraps depends on the width and the font.
        await page.WaitForFunctionAsync(
            "needle => window.monaco.editor.getModels().some(model => model.getValue().includes(needle))",
            ":: HomeBlaze.Components.ValueTile",
            new() { Timeout = 30000 });

        var lines = (await File.ReadAllTextAsync(GetPagePath(ScratchPage))).Replace("\r\n", "\n").Split('\n');
        var headLineIndex = Array.FindIndex(lines, line => line.StartsWith(":: HomeBlaze.Components.ValueTile", StringComparison.Ordinal));
        Assert.True(headLineIndex >= 0, "The page has no head line of a ValueTile.");
        Assert.Contains("Path=scratchmotor/CurrentSpeed", lines[headLineIndex]);
        Assert.Equal("::", lines[headLineIndex + 1]);
        Assert.Contains("Text before the block", lines);
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
    }

    private static ILocator GetTab(ILocator tabLayout, string title)
    {
        return tabLayout.GetByRole(AriaRole.Tab, new() { Name = title, Exact = true });
    }

    private static ILocator GetSelectedTab(ILocator tabLayout)
    {
        return tabLayout.GetByRole(AriaRole.Tab, new() { Selected = true });
    }

    private static async Task SelectEditModeAsync(IPage page, string mode)
    {
        await page.GetByTestId("edit-mode-menu").ClickAsync();
        await page.Locator(".mud-popover-open").GetByText(mode, new() { Exact = true }).ClickAsync();
    }

    private static string GetPagePath(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "Demo", name);
    }

    private static string CreateTab(string id, string title, string? content = null)
    {
        return $"::: Tab#{id} Title=\"{title}\"\n{content ?? $"Content of {id}"}\n:::\n";
    }

    // The layout has a name, so it stays the same subject when a block is inserted before it.
    private static string CreateTabsPage(string contentBefore, params string[] tabs)
    {
        return "---\ntitle: Scratch\n---\n\n# Scratch\n\n" + contentBefore +
            ":: HomeBlaze.Components.TabLayout#tabs\n" + string.Concat(tabs) + "::\n";
    }

    // Three tabs, the first with a paragraph that is longer than a line.
    private static string CreatePositionedTabsPage(string? position)
    {
        var argument = position is null ? string.Empty : $" Position={position}";
        var longText = string.Join(" ", Enumerable.Repeat("Content of alpha", 60));
        return "---\ntitle: Scratch\n---\n\n# Scratch\n\n" +
            $":: HomeBlaze.Components.TabLayout#tabs{argument}\n" +
            CreateTab("alpha", "Alpha", longText) + CreateTab("beta", "Beta") + CreateTab("gamma", "Gamma") + "::\n";
    }

    // Waits until the tab bar of the only tab layout stands beside the shown panel, or above it.
    private static Task WaitForTabBarAsync(IPage page, bool beside)
    {
        return page.WaitForFunctionAsync(
            """
            beside => {
                const layout = document.querySelector('[data-testid=tab-layout]');
                const bar = layout.querySelector('.mud-tabs-tabbar').getBoundingClientRect();
                const panel = [...layout.querySelectorAll('[data-testid=tab-panel]')]
                    .map(element => element.getBoundingClientRect())
                    .find(rectangle => rectangle.width > 0);
                if (!panel) {
                    return false;
                }
                return beside ? bar.right <= panel.left || bar.left >= panel.right : bar.bottom <= panel.top;
            }
            """,
            beside,
            new() { Timeout = 30000 });
    }

    private Task WriteScratchPageAsync(string content) => _fixture.WriteDemoPageAsync(ScratchPage, content);

    private async Task<IPage> OpenScratchPageWithSecondOfThreeTabsSelectedAsync()
    {
        await WriteScratchPageAsync(CreateTabsPage(
            string.Empty,
            CreateTab("alpha", "Alpha"),
            CreateTab("beta", "Beta"),
            CreateTab("gamma", "Gamma")));

        var page = await OpenPageAsync(ScratchPage);
        var tabs = page.GetByTestId("tab-layout");
        await Assertions.Expect(tabs.GetByText("Content of alpha")).ToBeVisibleAsync(new() { Timeout = 30000 });

        await GetTab(tabs, "Beta").ClickAsync();
        await Assertions.Expect(tabs.GetByText("Content of beta")).ToBeVisibleAsync();
        await Assertions.Expect(GetSelectedTab(tabs)).ToHaveTextAsync("Beta");
        return page;
    }

    private sealed record Box(double Left, double Top, double Right, double Bottom);

    // All boxes come from one call, so that a layout that is still moving cannot give boxes of different moments.
    private static async Task<Box[]> GetBoxesAsync(ILocator locator)
    {
        var rectangles = await locator.EvaluateAllAsync<double[][]>(
            """
            elements => elements.map(element => {
                const rectangle = element.getBoundingClientRect();
                return [rectangle.left, rectangle.top, rectangle.right, rectangle.bottom];
            })
            """);

        return rectangles.Select(values => new Box(values[0], values[1], values[2], values[3])).ToArray();
    }

    // The height that the children of an element take, without its own padding.
    private static Task<double> GetContentHeightAsync(ILocator locator)
    {
        return locator.EvaluateAsync<double>(
            """
            element => {
                const style = getComputedStyle(element);
                return element.getBoundingClientRect().height - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom);
            }
            """);
    }

    // The page content without the space of the navigation drawer, which no longer sits beside it at this width.
    private static bool IsNarrowContentWidth(double width) => width is > 300 and <= NarrowViewportWidth;

    // The navigation drawer gives up its space in a transition, so the content reaches its width a
    // moment after a resize. Waits until the width is as expected and no longer changes between frames.
    private static async Task WaitForLayoutAsync(IPage page, string testId, Func<double, bool> hasExpectedWidth)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var width = double.NaN;
        var stableWidth = double.NaN;

        while (DateTime.UtcNow < deadline)
        {
            width = await page.GetByTestId(testId).First.EvaluateAsync<double>(
                """
                element => new Promise(resolve => requestAnimationFrame(
                    () => requestAnimationFrame(() => resolve(element.getBoundingClientRect().width))))
                """);

            if (hasExpectedWidth(width) && Math.Abs(width - stableWidth) < 0.5)
            {
                return;
            }

            stableWidth = width;
        }

        Assert.Fail($"The layout '{testId}' did not settle at an expected width, the last width was {width}.");
    }

    private static Task<bool> HasHorizontalScrollingAsync(IPage page)
    {
        return page.EvaluateAsync<bool>(
            """
            () => [document.scrollingElement, ...document.querySelectorAll('.mud-main-content, .mud-layout')]
                .some(element => element.scrollWidth > element.clientWidth)
            """);
    }
}
