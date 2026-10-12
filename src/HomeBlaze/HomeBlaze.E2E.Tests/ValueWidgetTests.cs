using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the card options and the value widgets on markdown pages.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class ValueWidgetTests
{
    private const int Timeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public ValueWidgetTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenPageAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Values.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        return page;
    }

    private static ILocator GetCard(IPage page, string title) =>
        page.GetByTestId("card").Filter(new() { HasText = title });

    private static ILocator GetRow(IPage page, string label) =>
        page.GetByTestId("value-row").Filter(new() { Has = page.GetByTestId("value-label").GetByText(label, new() { Exact = true }) });

    private static ILocator GetPanel(IPage page, string title) =>
        page.GetByTestId("expansion-panel").Filter(new() { HasText = title });

    // A progress in the content of a row is a bar too, and the test of the share means the one on its own.
    private static ILocator GetBarOutsideRows(IPage page) =>
        page.Locator("[data-testid=progress]:not([data-testid=value-row] *)");

    // The path of an icon is what tells two icons of MudBlazor apart.
    private static Task<string?> GetIconShapeAsync(ILocator row) =>
        row.Locator(".mud-list-item-icon svg path").Last.GetAttributeAsync("d");

    [Fact]
    public async Task WhenCardHasHeaderOptions_ThenHeaderShowsThem()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var card = GetCard(page, "Linked Card");
        await Assertions.Expect(card.GetByTestId("card-subtitle")).ToHaveTextAsync("Card subtitle", new() { Timeout = Timeout });
        await Assertions.Expect(card.GetByTestId("card-icon")).ToBeVisibleAsync();
        await Assertions.Expect(card.GetByTestId("card-header-right")).ToContainTextAsync("Corner text");
        await Assertions.Expect(card).ToContainTextAsync("Body of the linked card.");
        await Assertions.Expect(card).ToHaveClassAsync(new Regex("mud-paper-outlined"));
    }

    [Fact]
    public async Task WhenCardHasLink_ThenTitleLeadsToThePageNextToIt()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var title = GetCard(page, "Linked Card").GetByTestId("card-title");
        await Assertions.Expect(title).ToHaveAttributeAsync("href", "/pages/Demo/Blocks.md", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenCardHasOnlyATitle_ThenItHasNoLinkAndNoHeaderParts()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var card = GetCard(page, "Plain Card");
        await Assertions.Expect(card.GetByTestId("card-title")).ToHaveTextAsync("Plain Card", new() { Timeout = Timeout });
        await Assertions.Expect(card.Locator("a")).ToHaveCountAsync(0);
        await Assertions.Expect(card.GetByTestId("card-subtitle")).ToHaveCountAsync(0);
        await Assertions.Expect(card.GetByTestId("card-header-right")).ToHaveCountAsync(0);
        await Assertions.Expect(card.GetByTestId("card-footer")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenCardHasNoOptions_ThenItIsOutlinedWithoutAShadow()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var card = GetCard(page, "Plain Card");
        await Assertions.Expect(card).ToHaveClassAsync(new Regex("mud-paper-outlined"), new() { Timeout = Timeout });
        await Assertions.Expect(card).Not.ToHaveClassAsync(new Regex(@"mud-elevation-[1-9]"));
    }

    [Fact]
    public async Task WhenCardIsNotOutlined_ThenItHasAShadowAndNoBorder()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var card = GetCard(page, "Raised Card");
        await Assertions.Expect(card).ToHaveClassAsync(new Regex(@"mud-elevation-[1-9]"), new() { Timeout = Timeout });
        await Assertions.Expect(card).Not.ToHaveClassAsync(new Regex("mud-paper-outlined"));
        await Assertions.Expect(card).Not.ToHaveClassAsync(new Regex("mud-elevation-0"));
    }

    [Fact]
    public async Task WhenPageRenders_ThenItShowsNoBlockErrors()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetCard(page, "Plain Card")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenRowHasPathAndLabel_ThenItShowsTheFormattedValue()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetRow(page, "Soll").GetByTestId("value-text")).ToHaveTextAsync("800", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenRowHasNoLabel_ThenLabelIsThePropertyAndValueHasItsUnit()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var row = GetRow(page, "Temperature");
        await Assertions.Expect(row.GetByTestId("value-text")).ToHaveTextAsync(new Regex(@"^-?\d+(\.\d+)?°C$"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenValueChanges_ThenRowUpdatesLive()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetRow(page, "Ist").GetByTestId("value-text")).ToHaveTextAsync(new Regex(@"^[1-9]\d*$"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenRowHasMap_ThenItShowsTheTextOfTheCase()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetRow(page, "Zustand").GetByTestId("value-text")).ToHaveTextAsync(new Regex("^(steht|startet|läuft|stoppt)$"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenPathLeadsNowhere_ThenRowShowsADashAndNoError()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var row = GetRow(page, "Fehlt");
        await Assertions.Expect(row.GetByTestId("value-missing")).ToHaveTextAsync("–", new() { Timeout = Timeout });
        await Assertions.Expect(row.GetByTestId("value-text")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenRowHasContentAndNoPath_ThenItShowsTheContentOnly()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var row = GetRow(page, "Zwei Werte");
        await Assertions.Expect(row).ToContainTextAsync("800 von 800", new() { Timeout = Timeout });
        await Assertions.Expect(row.GetByTestId("value-text")).ToHaveCountAsync(0);
        await Assertions.Expect(row.GetByTestId("value-missing")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenPageHasValueTile_ThenItShowsLabelAndValue()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var tile = page.GetByTestId("value-tile");
        await Assertions.Expect(tile.GetByTestId("value-label")).ToHaveTextAsync("Tile Target", new() { Timeout = Timeout });
        await Assertions.Expect(tile.GetByTestId("value-text")).ToHaveTextAsync("800");
    }

    [Fact]
    public async Task WhenCaseHasColor_ThenValueTextHasThatColor()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetRow(page, "Zustand").GetByTestId("value-text")).ToHaveClassAsync(new Regex(@"\bmud-(error|warning|success)-text\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenCaseHasIcon_ThenItReplacesTheIconOfTheRow()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        await Assertions.Expect(GetRow(page, "Lampe").GetByTestId("value-text")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        var rowIcon = await GetIconShapeAsync(GetRow(page, "Lampe"));
        var lightbulb = await GetIconShapeAsync(GetRow(page, "Lampe fest"));
        var speed = await GetIconShapeAsync(GetRow(page, "Soll"));
        Assert.Equal(lightbulb, rowIcon);
        Assert.NotEqual(speed, rowIcon);
    }

    [Fact]
    public async Task WhenOnlyOtherRowsHaveAnIcon_ThenTheLabelsStillLineUp()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        await Assertions.Expect(GetRow(page, "Ohne Symbol").GetByTestId("value-text")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        var withIcon = await GetRow(page, "Lampe").GetByTestId("value-label").BoundingBoxAsync();
        var withoutIcon = await GetRow(page, "Ohne Symbol").GetByTestId("value-label").BoundingBoxAsync();
        Assert.Equal(withIcon!.X, withoutIcon!.X);
    }

    [Fact]
    public async Task WhenNoRowHasAnIcon_ThenLabelsStartAtTheEdge()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var list = page.GetByTestId("value-list").Filter(new() { Has = page.GetByTestId("value-label").GetByText("Nur Text", new() { Exact = true }) });
        await Assertions.Expect(list.GetByTestId("value-text").First).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        var listBox = await list.BoundingBoxAsync();
        var label = await list.GetByTestId("value-label").First.BoundingBoxAsync();
        Assert.True(label!.X - listBox!.X < 40);
    }

    [Fact]
    public async Task WhenIndicatorIsAChip_ThenItShowsTheTextOfTheCase()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var chip = page.GetByTestId("indicator").First;
        await Assertions.Expect(chip).ToHaveTextAsync(new Regex("^(steht|startet|läuft|stoppt)$"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenIndicatorIsAnIcon_ThenItShowsNoText()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var icon = page.GetByTestId("indicator").Nth(1);
        await Assertions.Expect(icon).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(icon).ToHaveTextAsync(string.Empty);
    }

    [Fact]
    public async Task WhenIndicatorHasNoValue_ThenChipShowsADashWithThePathAsTooltip()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var chip = page.GetByTestId("indicator").Nth(2);
        await Assertions.Expect(chip).ToHaveTextAsync("–", new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);

        // The map error that the page shows is that of the indicator with a map that does not exist.
        await Assertions.Expect(page.GetByTestId("value-map-error")).ToHaveCountAsync(1);

        await chip.HoverAsync();
        await Assertions.Expect(page.Locator(".mud-tooltip").GetByText("valuemotor/Missing")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenMapOfAWidgetDoesNotExist_ThenTheWidgetShowsTheError()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("value-map-error")).ToHaveTextAsync("Cannot resolve map: nomap", new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("indicator")).ToHaveCountAsync(3);
    }

    [Fact]
    public async Task WhenProgressHasARange_ThenBarShowsTheShareOfTheValue()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var bar = GetBarOutsideRows(page);
        await Assertions.Expect(bar).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await Assertions.Expect(bar).ToHaveAttributeAsync("data-percent", "50");
    }

    [Fact]
    public async Task WhenRowHasAProgressAsContent_ThenTheBarHasAWidthAndStandsBeforeTheValue()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var row = GetRow(page, "Balken");
        await Assertions.Expect(row.GetByTestId("value-text")).ToHaveTextAsync("800", new() { Timeout = Timeout });

        // Assert
        var bar = row.GetByTestId("progress");
        await Assertions.Expect(bar).ToBeVisibleAsync();
        var barBox = await bar.BoundingBoxAsync();
        var valueBox = await row.GetByTestId("value-text").BoundingBoxAsync();
        Assert.True(barBox!.Width >= 100, $"The bar is {barBox.Width} pixels wide");
        Assert.True(barBox.X + barBox.Width <= valueBox!.X, $"The bar ends at {barBox.X + barBox.Width}, the value starts at {valueBox.X}");
    }

    [Fact]
    public async Task WhenCardHasImageAndFooter_ThenBothAreShown()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var card = GetCard(page, "Picture Card");
        await Assertions.Expect(card.GetByTestId("card-footer")).ToHaveTextAsync("Picture card footer", new() { Timeout = Timeout });
        var media = card.Locator(".mud-card-media");
        await Assertions.Expect(media).ToHaveCountAsync(1);
        await Assertions.Expect(media).ToHaveAttributeAsync("style", new Regex(@"background-image:\s*url\(.*/favicon\.ico"));
    }

    [Fact]
    public async Task WhenPanelIsExpandedOnThePage_ThenItsContentIsVisible()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var content = GetPanel(page, "Open Panel").GetByTestId("expansion-panel-content");
        await Assertions.Expect(content).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(content).ToContainTextAsync("Open panel target 800");
    }

    [Fact]
    public async Task WhenClosedPanelIsOpened_ThenItsContentShowsALiveValue()
    {
        // Arrange
        var page = await OpenPageAsync();
        var panel = GetPanel(page, "Closed Panel");
        var content = panel.GetByTestId("expansion-panel-content");
        await Assertions.Expect(content).ToBeAttachedAsync(new() { Timeout = Timeout });
        await Assertions.Expect(content).ToBeHiddenAsync();

        // Act
        await panel.GetByText("Closed Panel", new() { Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(content).ToBeVisibleAsync();
        await Assertions.Expect(content).ToContainTextAsync(new Regex(@"speed [1-9]\d* RPM"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenLayoutIsSingle_ThenOpeningAPanelClosesTheOther()
    {
        // Arrange
        var page = await OpenPageAsync();
        var first = GetPanel(page, "Exclusive A").GetByTestId("expansion-panel-content");
        var second = GetPanel(page, "Exclusive B").GetByTestId("expansion-panel-content");
        await Assertions.Expect(first).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(second).ToBeHiddenAsync();

        // Act
        await GetPanel(page, "Exclusive B").GetByText("Exclusive B", new() { Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(second).ToBeVisibleAsync();
        await Assertions.Expect(first).ToBeHiddenAsync();
    }

    [Fact]
    public async Task WhenPageHasAlert_ThenItShowsItsTextAndTheIconOfItsSeverity()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var alert = page.GetByTestId("alert");
        await Assertions.Expect(alert).ToContainTextAsync("Alert text", new() { Timeout = Timeout });
        await Assertions.Expect(alert).ToHaveClassAsync(new Regex("mud-alert-text-warning"));
        await Assertions.Expect(alert.Locator("svg")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task WhenAlertHasAOneLineText_ThenTheTextIsCentredNextToTheIcon()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var alert = page.GetByTestId("alert");
        await Assertions.Expect(alert).ToContainTextAsync("Alert text", new() { Timeout = Timeout });

        // Assert
        var icon = await alert.Locator("svg").BoundingBoxAsync();
        var text = await alert.Locator("p").BoundingBoxAsync();
        Assert.InRange(Math.Abs((icon!.Y + icon.Height / 2) - (text!.Y + text.Height / 2)), 0, 4);
    }
}
