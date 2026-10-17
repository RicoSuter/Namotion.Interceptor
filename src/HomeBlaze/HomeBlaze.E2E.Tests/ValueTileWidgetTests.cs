using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Samples;
using HomeBlaze.Storage.Files;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the tile kind of the value tile, the gauge in a layout and the history that value widgets open.
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

    private async Task<IPage> OpenPageAsync()
    {
        var page = await _fixture.CreatePageAsync();
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

        // Assert: the ring and the label together are centered, so the middle of the ring is half the label higher.
        var node = gauge.Locator("xpath=ancestor::div[contains(@class,'canvas-node')][1]");
        var nodeCenter = await GetCenterAsync(node);
        var ringCenter = await GetCenterAsync(gauge.GetByTestId("gauge-meter"));
        var labelBox = await PageGeometry.GetBoxAsync(gauge.GetByTestId("value-label"));
        Assert.InRange(ringCenter[0] - nodeCenter[0], -1.5, 1.5);
        Assert.InRange(nodeCenter[1] - ringCenter[1] - (labelBox.Height + 4) / 2, -1.5, 1.5);
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
}
