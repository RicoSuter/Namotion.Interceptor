using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Samples;
using HomeBlaze.Storage.Files;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the tile kind of the value tile, tiles in small places, the gauge in a layout and the history that
/// value widgets open.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class ValueTileWidgetTests
{
    private const int Timeout = 30000;
    private const string PageName = "ValueTiles.md";

    private readonly PlaywrightFixture _fixture;

    public ValueTileWidgetTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private Motor GetMotor() =>
        (Motor)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children["valuemotor"];

    private async Task<IPage> OpenPageAsync(int? width = null)
    {
        var page = await _fixture.CreatePageAsync();

        // Before the page opens: a narrower window closes the navigation drawer with a transition, which the page
        // follows for a moment after the resize.
        if (width is { } viewportWidth)
        {
            await page.SetViewportSizeAsync(viewportWidth, 900);
        }

        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync(Timeout);
        return page;
    }

    private static ILocator GetValueTile(IPage page, string label) =>
        page.GetByTestId("value-tile").Filter(new() { Has = page.GetByTestId("tile-subtitle").GetByText(label, new() { Exact = true }) });

    private static ILocator GetGauge(IPage page, string label) =>
        page.GetByTestId("gauge").Filter(new() { Has = page.GetByTestId("value-label").GetByText(label, new() { Exact = true }) });

    private static ILocator GetHistoryDialog(IPage page) => page.GetByTestId("property-history-dialog");

    private static Task<string> GetBackgroundAsync(ILocator tile) =>
        tile.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor");

    private static Task<double[]> GetCenterAsync(ILocator element) =>
        element.EvaluateAsync<double[]>("element => { const box = element.getBoundingClientRect(); return [box.x + box.width / 2, box.y + box.height / 2]; }");

    [Fact]
    public async Task WhenPageRenders_ThenItShowsNoBlockErrors()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("value-tile").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenValueTileIsATileInAGridCell_ThenItFillsTheCellAndShowsTheValueAboveTheLabel()
    {
        // Arrange
        GetMotor().TargetSpeed = 800;

        // Act
        var page = await OpenPageAsync();
        var tile = GetValueTile(page, "Target");
        await Assertions.Expect(tile.GetByTestId("tile-label")).ToHaveTextAsync("800", new() { Timeout = Timeout });

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-icon")).ToBeVisibleAsync();
        var valueBox = await PageGeometry.GetBoxAsync(tile.GetByTestId("tile-label"));
        var labelBox = await PageGeometry.GetBoxAsync(tile.GetByTestId("tile-subtitle"));
        Assert.True(valueBox.Top < labelBox.Top);

        var tileBox = await PageGeometry.GetBoxAsync(tile);
        var cellBox = await PageGeometry.GetBoxAsync(tile.Locator("xpath=ancestor::div[contains(@class,'grid-cell')][1]"));
        Assert.InRange(cellBox.Width - tileBox.Width, 0, 2.5);
        Assert.InRange(cellBox.Height - tileBox.Height, 0, 2.5);
    }

    [Fact]
    public async Task WhenMapMatchesTheValueOfATile_ThenTheTileIsFilledWithTheColorOfTheCase()
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = GetValueTile(page, "Powered");
        await Assertions.Expect(tile.GetByTestId("tile-label")).ToHaveTextAsync("Running", new() { Timeout = Timeout });
        var warning = await page.ResolveColorAsync("var(--mud-palette-warning)");
        Assert.Equal(warning, await GetBackgroundAsync(tile));

        // Act
        GetMotor().IsPowered = false;

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-label")).Not.ToHaveTextAsync("Running", new() { Timeout = Timeout });
        var surface = await page.ResolveColorAsync("var(--mud-palette-surface)");
        Assert.Equal(surface, await GetBackgroundAsync(tile));
    }

    [Fact]
    public async Task WhenTilePropertyHasAHistory_ThenAClickOnTheTileOpensIt()
    {
        // Arrange
        var page = await OpenPageAsync();
        var tile = GetValueTile(page, "Target");
        var button = tile.GetByRole(AriaRole.Button);
        await Assertions.Expect(button).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await button.ClickAsync();

        // Assert
        await Assertions.Expect(GetHistoryDialog(page)).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTilePathLeadsNowhere_ThenItShowsTheDashAndIsNoButton()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var tile = GetValueTile(page, "Missing");

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-label")).ToHaveTextAsync("–", new() { Timeout = Timeout });
        await Assertions.Expect(tile.GetByRole(AriaRole.Button)).ToHaveCountAsync(0);
    }

    [Theory]
    [InlineData("Box target")]
    [InlineData("Canvas gauge")]
    [InlineData("List target")]
    public async Task WhenValueWidgetPropertyHasAHistory_ThenItsLabelOpensIt(string label)
    {
        // Arrange
        var page = await OpenPageAsync();
        var button = page.GetByTestId("history-label-button").GetByText(label, new() { Exact = true });
        await Assertions.Expect(button).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await button.ClickAsync();

        // Assert
        await Assertions.Expect(GetHistoryDialog(page)).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenGaugeIsTheOnlyContentOfACanvasNode_ThenItIsCenteredInTheNode()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var gauge = GetGauge(page, "Canvas gauge");
        await Assertions.Expect(gauge).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert: the ring and the label together are centered, so the middle of the ring is half the label and the gap of 8
        // pixels higher.
        var node = gauge.Locator("xpath=ancestor::div[contains(@class,'canvas-node')][1]");
        var nodeCenter = await GetCenterAsync(node);
        var ringCenter = await GetCenterAsync(gauge.GetByTestId("gauge-meter"));
        var labelBox = await PageGeometry.GetBoxAsync(gauge.GetByTestId("value-label"));
        Assert.InRange(ringCenter[0] - nodeCenter[0], -1.5, 1.5);
        Assert.InRange(nodeCenter[1] - ringCenter[1] - (labelBox.Height + 8) / 2, -1.5, 1.5);
    }

    [Fact]
    public async Task WhenGaugesOfDifferentSizesStandInAFlowRow_ThenTheyStartAtTheTopAndTheStartOfTheirItems()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var small = GetGauge(page, "Flow small");
        var large = GetGauge(page, "Flow large");
        await Assertions.Expect(small).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        var smallBox = await PageGeometry.GetBoxAsync(small);
        var largeBox = await PageGeometry.GetBoxAsync(large);
        var itemBox = await PageGeometry.GetBoxAsync(small.Locator("xpath=ancestor::div[contains(@class,'flow-layout-item')][1]"));
        Assert.InRange(smallBox.Top - largeBox.Top, -0.5, 0.5);
        Assert.InRange(smallBox.Left - itemBox.Left, -0.5, 0.5);
        Assert.True(smallBox.Height < largeBox.Height);
    }

    [Fact]
    public async Task WhenGridCellIsTooLowForTheIconAndTheTextOfATile_ThenTheTileLeavesOutTheIconAndKeepsItsText()
    {
        // Arrange & Act: four square cells on a phone are about 80 pixels high, lower than the icon and two lines.
        var page = await OpenPageAsync(width: 390);
        var tile = GetValueTile(page, "Target");
        await Assertions.Expect(tile.GetByTestId("tile-label")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-icon")).ToBeHiddenAsync(new() { Timeout = Timeout });
        var tileBox = await PageGeometry.GetBoxAsync(tile);
        var labelBox = await PageGeometry.GetBoxAsync(tile.GetByTestId("tile-subtitle"));
        Assert.True(labelBox.Top + labelBox.Height <= tileBox.Top + tileBox.Height);
    }

    [Fact]
    public async Task WhenGridCellIsTooLowForATileThatShowsOnlyItsIcon_ThenTheTileKeepsTheIcon()
    {
        // Arrange & Act
        var page = await OpenPageAsync(width: 390);
        var tile = page.GetByTestId("operation-button-tile");

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-icon")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(tile.GetByTestId("tile-label")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenTheLibraryDrawsNoBoxUnderItsNodes()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        await Assertions.Expect(GetGauge(page, "Canvas gauge")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert: the library draws a light rect under every node, whose stroke would stand out at the edges of the node.
        var paints = await page.Locator(".canvas-diagram > svg rect").EvaluateAllAsync<string[]>(
            "rects => rects.map(rect => getComputedStyle(rect).stroke + ' ' + getComputedStyle(rect).fill)");
        Assert.NotEmpty(paints);
        Assert.All(paints, paint => Assert.Contains(paint, new[] { "rgba(0, 0, 0, 0) rgba(0, 0, 0, 0)", "rgba(0, 0, 0, 0) none" }));
    }

    [Fact]
    public async Task WhenTileFillsALargeGridCell_ThenItsTextIsLargerThanThatOfATileAloneOnThePage()
    {
        // Arrange & Act: four square cells on a wide page are about 240 pixels.
        var page = await OpenPageAsync(width: 1280);
        var inCell = GetValueTile(page, "Target").GetByTestId("tile-label");
        var alone = GetValueTile(page, "Alone").GetByTestId("tile-label");
        await Assertions.Expect(alone).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        static Task<double> GetFontSizeAsync(ILocator element) =>
            element.EvaluateAsync<double>("element => parseFloat(getComputedStyle(element).fontSize)");
        Assert.Equal(18, await GetFontSizeAsync(alone), 0.5);
        Assert.True(await GetFontSizeAsync(inCell) > 24);
    }

    [Fact]
    public async Task WhenGridCellIsLowerThanAGauge_ThenTheRingGetsSmallerAndTheGaugeFitsTheCell()
    {
        // Arrange & Act: four square cells on a phone are about 80 pixels, lower than the medium ring of 112 pixels.
        var page = await OpenPageAsync(width: 390);
        var gauge = GetGauge(page, "Grid gauge");
        await Assertions.Expect(gauge).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        var cellBox = await PageGeometry.GetBoxAsync(gauge.Locator("xpath=ancestor::div[contains(@class,'grid-cell')][1]"));
        var ringBox = await PageGeometry.GetBoxAsync(gauge.GetByTestId("gauge-meter"));
        var labelBox = await PageGeometry.GetBoxAsync(gauge.GetByTestId("value-label"));
        Assert.True(ringBox.Height < 112);
        Assert.Equal(ringBox.Width, ringBox.Height, 1);
        Assert.True(ringBox.Top >= cellBox.Top);
        Assert.True(labelBox.Top + labelBox.Height <= cellBox.Top + cellBox.Height);
    }

    [Theory]
    [InlineData(375, true)] // narrower than the MinWidth of 600: drawn at 600, and the frame scrolls
    [InlineData(1280, false)] // wider: fits the width
    public async Task WhenCanvasThatFitsTheWidthHasAMinWidth_ThenItIsDrawnAtLeastThatWideAndScrollsOnANarrowerPage(int width, bool scrolls)
    {
        // Arrange
        var page = await OpenPageAsync(width);
        var tile = GetValueTile(page, "Min canvas tile");
        await Assertions.Expect(tile).ToBeVisibleAsync(new() { Timeout = Timeout });
        var frame = tile.Locator("xpath=ancestor::div[@data-testid='canvas-scale-frame'][1]");
        var node = tile.Locator("xpath=ancestor::div[contains(concat(' ', normalize-space(@class), ' '), ' canvas-node ')][1]");

        // Act: the node, with its border, is half of the 400 pixel design width, so it is half of the drawn width once the
        // frame is measured.
        var drawnWidth = await frame.EvaluateAsync<double>("frame => Math.max(frame.clientWidth, 600)");
        await page.WaitForFunctionAsync(
            "([node, expected]) => Math.abs(node.getBoundingClientRect().width - expected) < 2",
            new object[] { await node.ElementHandleAsync(), drawnWidth / 2 },
            new() { Timeout = Timeout });

        // Assert
        var overflow = await frame.EvaluateAsync<double>("frame => frame.scrollWidth - frame.clientWidth");
        if (scrolls)
        {
            Assert.InRange(overflow, 600 - await frame.EvaluateAsync<double>("frame => frame.clientWidth") - 1, 600);
        }
        else
        {
            Assert.InRange(overflow, 0, 0.5);
        }
    }

    [Fact]
    public async Task WhenPageIsEdited_ThenValueWidgetsOfferNoHistory()
    {
        // Arrange
        var page = await OpenPageAsync();
        var tile = GetValueTile(page, "Target");
        await Assertions.Expect(tile.GetByRole(AriaRole.Button)).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("history-label-button").First).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await page.SelectEditModeAsync("Inline");

        // Assert: a click selects the widget instead, so neither the tile nor a label is a button of its own.
        await Assertions.Expect(tile.GetByRole(AriaRole.Button)).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("history-label-button")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }
}
