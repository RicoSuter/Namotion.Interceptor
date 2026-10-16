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
        await page.WaitUntilInteractiveAsync();
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
    public async Task WhenTwoCardsFollowEachOther_ThenTheyKeepTheSpacingOfBlocks()
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

        // Assert: 1rem, the space between the blocks of a page.
        Assert.Equal(16.0, gap, 1.0);
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
        var (itemHeight, cardHeight) = await GetContentAndCardHeightsAsync(item);

        // Assert: the margin that separates cards on a page would make the item higher than its card.
        Assert.Equal(cardHeight, itemHeight, 1.0);
    }

    [Fact]
    public async Task WhenACardIsTheOnlyContentOfATab_ThenThePanelAddsOnlyItsPadding()
    {
        // Arrange
        var page = await OpenPageAsync("Layouts.md");
        var panel = page.GetByTestId("tab-panel").First;
        await Assertions.Expect(panel.GetByTestId("card")).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Act
        var (panelHeight, cardHeight) = await GetContentAndCardHeightsAsync(panel);

        // Assert
        Assert.Equal(cardHeight, panelHeight, 1.0);
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
    public async Task WhenTabLayoutHasAPositionOrNone_ThenTheTabBarIsOnThatSideOrAtTheTopAndTheContentTakesTheRest(string? position)
    {
        // Arrange: the first tab has a line of code that cannot wrap, the second one a few words.
        await WriteScratchPageAsync(CreatePositionedTabsPage(position));
        var page = await OpenPageAsync(ScratchPage);
        await page.SetViewportSizeAsync(1280, 800);
        var tabs = page.GetByTestId("tab-layout");
        await Assertions.Expect(tabs.GetByText("Content of alpha").First).ToBeVisibleAsync(new() { Timeout = 30000 });
        await WaitForLayoutAsync(page, "tab-layout", width => width > 800);
        var isAtASide = position is "Left" or "Right";
        await WaitForTabBarAsync(page, isAtASide ? Beside : position == "Bottom" ? Below : Above);

        // Act
        var layout = (await GetBoxesAsync(tabs)).Single();
        var bar = (await GetBoxesAsync(tabs.Locator(".mud-tabs-tabbar"))).Single();
        var widePanel = (await GetBoxesAsync(tabs.GetByTestId("tab-panel").First)).Single();
        var buttons = await GetBoxesAsync(tabs.GetByRole(AriaRole.Tab));
        var titleStarts = await tabs.GetByRole(AriaRole.Tab).EvaluateAllAsync<double[]>(
            """
            elements => elements.map(element => {
                const range = document.createRange();
                range.selectNodeContents(element);
                return range.getBoundingClientRect().left;
            })
            """);
        var scrollsSideways = await HasHorizontalScrollingAsync(page);

        await GetTab(tabs, "Beta").ClickAsync();
        await Assertions.Expect(tabs.GetByText("Content of beta")).ToBeVisibleAsync();
        var narrowPanel = (await GetBoxesAsync(tabs.GetByTestId("tab-panel").Nth(1))).Single();

        // Assert: the tabs stand below each other at a side and next to each other above or below the content.
        Assert.True(isAtASide ? buttons[1].Top >= buttons[0].Bottom : buttons[1].Left >= buttons[0].Right, $"{buttons[0]} {buttons[1]}");
        if (isAtASide)
        {
            // Titles of different lengths start at the same place, which centred titles do not.
            Assert.All(titleStarts, start => Assert.Equal(titleStarts[0], start, 0.5));
        }
        Assert.True(position switch
        {
            "Left" => bar.Right <= widePanel.Left,
            "Right" => bar.Left >= widePanel.Right,
            "Bottom" => bar.Top >= widePanel.Bottom,
            _ => bar.Bottom <= widePanel.Top
        }, $"bar {bar}, panel {widePanel}");

        // The content takes the room that the tab bar leaves: no more for content that is too wide, no less for narrow content.
        var expectedWidth = layout.Right - layout.Left - (isAtASide ? bar.Right - bar.Left : 0);
        Assert.Equal(expectedWidth, widePanel.Right - widePanel.Left, 1.0);
        Assert.Equal(expectedWidth, narrowPanel.Right - narrowPanel.Left, 1.0);
        Assert.False(scrollsSideways);
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    public async Task WhenContentBesideTheTabBarCannotGetNarrowEnough_ThenItScrollsInsideItsPanelAndLeavesTheTabsFree(string position)
    {
        // Arrange: a word without a place to break, far wider than the window.
        await WriteScratchPageAsync(
            "---\ntitle: Scratch\n---\n\n# Scratch\n\n" +
            $":: HomeBlaze.Components.TabLayout#tabs Position={position}\n" +
            CreateTab("alpha", "Alpha", new string('x', 800)) + CreateTab("beta", "Beta") + "::\n");
        var page = await OpenPageAsync(ScratchPage);
        await page.SetViewportSizeAsync(1280, 800);
        var tabs = page.GetByTestId("tab-layout");
        await Assertions.Expect(tabs.GetByTestId("tab-panel").First).ToBeVisibleAsync(new() { Timeout = 30000 });
        await WaitForLayoutAsync(page, "tab-layout", width => width > 800);
        await WaitForTabBarAsync(page, Beside);

        // Act
        var panelScrolls = await tabs.GetByTestId("tab-panel").First.EvaluateAsync<bool>("panel => panel.scrollWidth > panel.clientWidth");
        var tabIsOnTop = await GetTab(tabs, "Beta").EvaluateAsync<bool>(
            """
            tab => {
                const box = tab.getBoundingClientRect();
                const top = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
                return tab.contains(top);
            }
            """);
        var scrollsSideways = await HasHorizontalScrollingAsync(page);

        // Assert
        Assert.True(panelScrolls);
        Assert.True(tabIsOnTop);
        Assert.False(scrollsSideways);
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    public async Task WhenAPageWithTabsAtASideOpensInANarrowWindow_ThenTheTabBarIsAtTheTop(string position)
    {
        // Arrange
        await WriteScratchPageAsync(CreatePositionedTabsPage(position));
        var page = await _fixture.CreatePageAsync(new() { ViewportSize = new() { Width = NarrowViewportWidth, Height = 844 } });

        // Act
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ScratchPage}");
        await page.WaitUntilInteractiveAsync();
        var tabs = page.GetByTestId("tab-layout");
        await Assertions.Expect(tabs.GetByText("Content of alpha").First).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Assert
        await WaitForTabBarAsync(page, Above);
        await WaitForLayoutAsync(page, "tab-layout", IsNarrowContentWidth);
        var buttons = await GetBoxesAsync(tabs.GetByRole(AriaRole.Tab));
        Assert.Equal(buttons[0].Top, buttons[1].Top, 1.0);
        Assert.False(await HasHorizontalScrollingAsync(page));
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    public async Task WhenTheWindowOfTabsAtASideGetsNarrowAndWideAgain_ThenTheTabBarFollowsAndTheSelectedTabStays(string position)
    {
        // Arrange
        await WriteScratchPageAsync(CreatePositionedTabsPage(position));
        var page = await OpenPageAsync(ScratchPage);
        await page.SetViewportSizeAsync(1280, 800);
        var tabs = page.GetByTestId("tab-layout");
        await Assertions.Expect(tabs.GetByText("Content of alpha").First).ToBeVisibleAsync(new() { Timeout = 30000 });
        await WaitForTabBarAsync(page, Beside);
        await GetTab(tabs, "Beta").ClickAsync();
        await Assertions.Expect(tabs.GetByText("Content of beta")).ToBeVisibleAsync();

        // Act: each wait ends when the tab bar is where the window width puts it.
        await page.SetViewportSizeAsync(NarrowViewportWidth, 844);
        await WaitForTabBarAsync(page, Above);
        await WaitForLayoutAsync(page, "tab-layout", IsNarrowContentWidth);
        var selectedWhenNarrow = await GetSelectedTab(tabs).InnerTextAsync();
        var scrollsSidewaysWhenNarrow = await HasHorizontalScrollingAsync(page);

        await page.SetViewportSizeAsync(1280, 800);
        await WaitForTabBarAsync(page, Beside);
        var selectedWhenWide = await GetSelectedTab(tabs).InnerTextAsync();

        // Assert
        Assert.Equal("BETA", selectedWhenNarrow.Trim(), ignoreCase: true);
        Assert.Equal("BETA", selectedWhenWide.Trim(), ignoreCase: true);
        Assert.False(scrollsSidewaysWhenNarrow);
        await Assertions.Expect(tabs.GetByText("Content of beta")).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Left")]
    public async Task WhenAPageWithATabLayoutRendersAgain_ThenTheTabLayoutDoesNotMeasureItsTabsAgainAndTheTabBarStays(string? position)
    {
        // Arrange: the temperature of the motor has noise, so the text of the page renders again with every simulation
        // step, and the tab layout below it with it.
        var argument = position is null ? string.Empty : $" Position={position}";
        await WriteScratchPageAsync(
            "---\ntitle: Scratch\n---\n\n# Scratch\n\nMotor temperature {{ ../Motor/Temperature }}\n\n" +
            $":: HomeBlaze.Components.TabLayout#tabs{argument}\n" +
            CreateTab("alpha", "Alpha") + CreateTab("beta", "Beta") + "::\n");

        var page = await OpenPageAsync(ScratchPage);
        await page.SetViewportSizeAsync(1280, 800);
        var liveText = page.Locator(".markdown-content p").Filter(new() { HasText = "Motor temperature" });
        await Assertions.Expect(liveText).ToContainTextAsync("°C", new() { Timeout = 30000 });
        await WaitForTabBarAsync(page, position is null ? Above : Beside);

        await page.EvaluateAsync(
            """
            () => {
                window.measuredElements = 0;
                const measure = Element.prototype.getBoundingClientRect;
                Element.prototype.getBoundingClientRect = function () {
                    if (this.closest('.mud-tabs-tabbar')) {
                        window.measuredElements++;
                    }
                    return measure.call(this);
                };
            }
            """);

        // The layout measures its tabs after its first renders. They are over when a render of the page adds no
        // measurement. A layout that measures on every render never gets there, and fails below.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var measuredBefore = await page.EvaluateAsync<int>("() => window.measuredElements");
            await WaitForOtherTextAsync(liveText);
            if (await page.EvaluateAsync<int>("() => window.measuredElements") == measuredBefore)
            {
                break;
            }
        }

        await page.EvaluateAsync("() => { window.measuredElements = 0; }");

        // Act
        await WaitForOtherTextAsync(liveText);
        await WaitForOtherTextAsync(liveText);
        var measuredElements = await page.EvaluateAsync<int>("() => window.measuredElements");

        // Assert
        Assert.Equal(0, measuredElements);
        await WaitForTabBarAsync(page, position is null ? Above : Beside);
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
        await page.SelectEditModeAsync("Inline");
        var actionBar = await card.Locator(".subject-component-wrapper.selectable").First.SelectWidgetAsync();
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();

        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByLabel("Target Speed (RPM)").FillAsync("1500");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await page.SelectEditModeAsync("View");

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
        await page.SelectEditModeAsync("Split");

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
        await page.SelectEditModeAsync("View");
        await Assertions.Expect(card).ToContainTextAsync("/ 1500 RPM", new() { Timeout = 30000 });
        await Assertions.Expect(GetCard(page, "Other Card")).ToContainTextAsync("The text of the other card.");
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);

        var written = (await File.ReadAllTextAsync(GetPagePath(ScratchPage))).Replace("\r\n", "\n");
        Assert.Equal(source.Replace("TargetSpeed=1200", "TargetSpeed=1500"), written);
    }

    [Fact]
    public async Task WhenBlockIsSavedInItsDialogAndThenThePageIsSaved_ThenTheSaveSucceedsAndEachRegionHasOneDecoration()
    {
        // Arrange
        const string source =
            "---\ntitle: Scratch\n---\n\n# Scratch\n\n" +
            ":: HomeBlaze.Samples.Motor#scratchmotor Name=ScratchMotor TargetSpeed=1200 SimulationInterval=00:00:01\n" +
            "::\n\nSpeed {{ scratchmotor/TargetSpeed }}\n";
        await WriteScratchPageAsync(source);

        var page = await OpenPageAsync(ScratchPage);
        await page.SelectEditModeAsync("Split");

        // The editor only knows its block regions once the decorations are applied.
        await Assertions.Expect(page.Locator(".subject-block-decoration").First).ToBeVisibleAsync(new() { Timeout = 30000 });
        var headLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "HomeBlaze.Samples.Motor#scratchmotor" });
        await Assertions.Expect(headLine).ToBeVisibleAsync(new() { Timeout = 30000 });
        await headLine.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit scratchmotor" }).ClickAsync();

        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByLabel("Target Speed (RPM)").FillAsync("1500");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        var writtenHeadLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "TargetSpeed=1500" });
        await Assertions.Expect(writtenHeadLine).ToBeVisibleAsync(new() { Timeout = 30000 });

        // The notification of the dialog is closed, because a notification with the same text is not shown twice.
        var savedNotification = page.Locator(".mud-snackbar").Filter(new() { HasText = "Configuration saved." });
        await Assertions.Expect(savedNotification).ToBeVisibleAsync(new() { Timeout = 30000 });
        await savedNotification.GetByRole(AriaRole.Button).ClickAsync();
        await Assertions.Expect(savedNotification).ToHaveCountAsync(0, new() { Timeout = 30000 });

        // Act
        await page.Locator("[data-testid='save-button'] button").ClickAsync();

        // Assert: the notification comes after the last refresh of the decorations. Refreshes that overlapped
        // would each have added their decorations, and one of them would have stayed beside the later ones.
        await Assertions.Expect(savedNotification).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(page.GetByText("Error saving configuration")).ToHaveCountAsync(0);

        var decorationCounts = await page.EvaluateAsync<int[]>(
            """
            () => {
                const decorations = blazorMonaco.editor.getEditor('markdown-editor').getModel().getAllDecorations();
                return ['subject-block-decoration', 'expression-decoration']
                    .map(className => decorations.filter(decoration => decoration.options.className === className).length);
            }
            """);
        Assert.Equal([1, 1], decorationCounts);

        var written = (await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(ScratchPage))).Replace("\r\n", "\n");
        Assert.Equal(source.Replace("TargetSpeed=1200", "TargetSpeed=1500"), written);
    }

    [Fact]
    public async Task WhenNameInTheCreateDialogIsNoValidBlockName_ThenCreateStaysDisabledUntilTheNameIsCorrected()
    {
        // Arrange
        await WriteScratchPageAsync("# Scratch\n\nText before the block\n");

        var page = await OpenPageAsync(ScratchPage);
        await Assertions.Expect(page.GetByText("Text before the block")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await page.SelectEditModeAsync("Split");

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
        await page.SelectEditModeAsync("Split");

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
        await page.SelectEditModeAsync("View");
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
        await page.SelectEditModeAsync("Split");

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

    [Fact]
    public async Task WhenThemeIsLight_ThenALinkHasThePrimaryColourOfTheTheme()
    {
        // Arrange
        var page = await OpenScratchPageWithLinkAsync();
        var link = page.Locator(".markdown-content a", new() { HasTextString = "Theme link" });

        // Act
        await page.GetByTitle("Toggle Dark Mode").ClickAsync();

        // Assert: #3f2ddd, the primary colour of the light palette.
        await Assertions.Expect(link).ToHaveCSSAsync("color", "rgb(63, 45, 221)");
        Assert.Equal("rgb(63, 45, 221)", await page.ResolveColourAsync("var(--mud-palette-primary)"));
    }

    [Fact]
    public async Task WhenThemeIsDark_ThenALinkHasTheLightenedPrimaryColourOfTheTheme()
    {
        // Arrange & Act: the app starts in the dark theme.
        var page = await OpenScratchPageWithLinkAsync();
        var link = page.Locator(".markdown-content a", new() { HasTextString = "Theme link" });
        await Assertions.Expect(page.Locator(".homeblaze-dark-mode")).ToHaveCountAsync(1);

        // Assert
        var expected = await page.ResolveColourAsync("var(--mud-palette-primary-lighten)");
        Assert.NotEqual(await page.ResolveColourAsync("var(--mud-palette-primary)"), expected);
        await Assertions.Expect(link).ToHaveCSSAsync("color", expected);
    }

    private async Task<IPage> OpenScratchPageWithLinkAsync()
    {
        await WriteScratchPageAsync("# Scratch\n\nText with a [Theme link](https://example.com).\n");
        var page = await OpenPageAsync(ScratchPage);
        await Assertions.Expect(page.GetByText("Theme link")).ToBeVisibleAsync(new() { Timeout = 30000 });
        return page;
    }

    private static ILocator GetTab(ILocator tabLayout, string title)
    {
        return tabLayout.GetByRole(AriaRole.Tab, new() { Name = title, Exact = true });
    }

    private static ILocator GetSelectedTab(ILocator tabLayout)
    {
        return tabLayout.GetByRole(AriaRole.Tab, new() { Selected = true });
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

    // Three tabs. The first has a paragraph that is longer than a line and a line of code that cannot wrap.
    private static string CreatePositionedTabsPage(string? position)
    {
        var argument = position is null ? string.Empty : $" Position={position}";
        var longText = string.Join(" ", Enumerable.Repeat("Content of alpha", 60));
        var longCode = "```\n" + new string('x', 4000) + "\n```";
        return "---\ntitle: Scratch\n---\n\n# Scratch\n\n" +
            $":: HomeBlaze.Components.TabLayout#tabs{argument}\n" +
            CreateTab("alpha", "Alpha", $"{longText}\n\n{longCode}") + CreateTab("beta", "Beta") + CreateTab("gamma", "Gamma") + "::\n";
    }

    // Waits until the element shows another text than it does now.
    private static async Task WaitForOtherTextAsync(ILocator locator)
    {
        var text = await locator.InnerTextAsync();
        await Assertions.Expect(locator).Not.ToHaveTextAsync(text, new() { Timeout = 30000, UseInnerText = true });
    }

    private const string Beside = "beside";
    private const string Above = "above";
    private const string Below = "below";

    // Waits until the tab bar of the only tab layout stands beside, above or below the shown panel.
    private static Task WaitForTabBarAsync(IPage page, string place)
    {
        return page.WaitForFunctionAsync(
            """
            place => {
                const layout = document.querySelector('[data-testid=tab-layout]');
                const bar = layout.querySelector('.mud-tabs-tabbar').getBoundingClientRect();
                const panel = [...layout.querySelectorAll('[data-testid=tab-panel]')]
                    .map(element => element.getBoundingClientRect())
                    .find(rectangle => rectangle.width > 0);
                if (!panel) {
                    return false;
                }
                return place === 'beside' ? bar.right <= panel.left || bar.left >= panel.right
                    : place === 'above' ? bar.bottom <= panel.top
                    : bar.top >= panel.bottom;
            }
            """,
            place,
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

    // The height that the children of an element take without its own padding, and the height of the card in it. Both
    // come from one call, so that a layout that is still moving cannot give heights of different moments.
    private static async Task<(double Content, double Card)> GetContentAndCardHeightsAsync(ILocator locator)
    {
        var heights = await locator.EvaluateAsync<double[]>(
            """
            element => {
                const style = getComputedStyle(element);
                const card = element.querySelector('[data-testid=card]');
                return [
                    element.getBoundingClientRect().height - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom),
                    card.getBoundingClientRect().height,
                ];
            }
            """);

        return (heights[0], heights[1]);
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
