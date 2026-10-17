using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the gauge, the subject table and the responsive block on a markdown page.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class DisplayWidgetTests
{
    private const int Timeout = 30000;

    private const string MissingText = "–";

    // The texts of the Responsive blocks of the page, each followed by " screen text".
    private static readonly string[] ResponsiveScreens = ["Phone", "Wide", "Tablet", "Large tablet", "Desktop"];

    private readonly PlaywrightFixture _fixture;

    public DisplayWidgetTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenPageAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Displays.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        await page.WaitUntilInteractiveAsync(Timeout);
        return page;
    }

    private static ILocator GetGauge(IPage page, string label) =>
        page.GetByTestId("gauge").Filter(new() { Has = page.GetByTestId("value-label").GetByText(label, new() { Exact = true }) });

    // The page has several tables of the same two motors: they are told apart by a header that only one of them has.
    private static ILocator GetTable(IPage page, string headerText) =>
        page.GetByTestId("subject-table").Filter(new() { Has = page.Locator("th").GetByText(headerText, new() { Exact = true }) });

    private static ILocator GetTableRow(ILocator table, string motorName) =>
        table.GetByTestId("subject-table-row").Filter(new() { Has = table.Page.GetByTestId("subject-table-link").GetByText(motorName, new() { Exact = true }) });

    [Fact]
    public async Task WhenPageRenders_ThenItShowsNoBlockErrors()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("subject-table").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenGaugeHasAValueAndARange_ThenItShowsTheValueAndItsShareAsAMeter()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var gauge = GetGauge(page, "Gauge Target");
        await Assertions.Expect(gauge.GetByTestId("value-text")).ToHaveTextAsync("1000", new() { Timeout = Timeout });
        await Assertions.Expect(gauge).ToHaveAttributeAsync("role", "meter");
        await Assertions.Expect(gauge).ToHaveAttributeAsync("aria-valuenow", "1000");
        await Assertions.Expect(gauge).ToHaveAttributeAsync("aria-valuemin", "0");
        await Assertions.Expect(gauge).ToHaveAttributeAsync("aria-valuemax", "2000");
        await Assertions.Expect(gauge).ToHaveAttributeAsync("aria-label", "Gauge Target");
        await Assertions.Expect(gauge).ToHaveAttributeAsync("data-percent", "50");
    }

    [Fact]
    public async Task WhenGaugeHasNoValue_ThenItShowsADashAndAnEmptyRingWithoutANumber()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var gauge = GetGauge(page, "Gauge Missing");
        await Assertions.Expect(gauge.GetByTestId("value-missing")).ToHaveTextAsync(MissingText, new() { Timeout = Timeout });
        await Assertions.Expect(gauge).Not.ToHaveAttributeAsync("aria-valuenow", new Regex(".*"));
        await Assertions.Expect(gauge).Not.ToHaveAttributeAsync("data-percent", new Regex(".*"));
        await Assertions.Expect(gauge).ToHaveAttributeAsync("role", "group");
    }

    [Fact]
    public async Task WhenGaugeHasNoRange_ThenItShowsTheArgumentErrorInItsPlace()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("widget-argument-error").Filter(new() { HasText = "Needs a range" }))
            .ToHaveTextAsync("Needs a range: set Minimum and Maximum", new() { Timeout = Timeout });
        await Assertions.Expect(GetGauge(page, "Gauge No Range")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenRangeOfAProgressIsInverted_ThenItShowsTheArgumentErrorInItsPlace()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("widget-argument-error").Filter(new() { HasText = "Maximum must be above Minimum" }))
            .ToHaveTextAsync(["Maximum must be above Minimum: 2000 to 0"], new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("progress")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenMapOfAGaugeOrOfATableColumnCannotBeResolved_ThenEachShowsOneArgumentErrorInItsPlace()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert: the table has two rows, and its broken column still shows one error and not one per cell.
        var errors = page.GetByTestId("widget-argument-error");
        await Assertions.Expect(errors.Filter(new() { HasText = "nogaugemap" })).ToHaveTextAsync(["Cannot resolve map: nogaugemap"], new() { Timeout = Timeout });
        await Assertions.Expect(errors.Filter(new() { HasText = "nocolumnmap" })).ToHaveTextAsync(["Cannot resolve map: nocolumnmap"]);
        await Assertions.Expect(GetGauge(page, "Gauge Broken Map")).ToHaveCountAsync(0);
        await Assertions.Expect(GetTable(page, "Broken Name")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenTableListsTheMotorsOfThePage_ThenItHasARowPerMotorWithItsValuesAndALink()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var table = GetTable(page, "Target");
        await Assertions.Expect(table.GetByTestId("subject-table-row")).ToHaveCountAsync(2, new() { Timeout = Timeout });
        await Assertions.Expect(table.Locator("th")).ToHaveTextAsync(new[] { "Motor", "Target" });

        var first = GetTableRow(table, "Display Motor");
        await Assertions.Expect(first.GetByTestId("subject-table-cell").Nth(1)).ToHaveTextAsync("1000");
        await Assertions.Expect(first.GetByTestId("subject-table-link")).ToHaveAttributeAsync("href", "/browser/Demo/Displays.md/dispmotor");

        var second = GetTableRow(table, "Second Motor");
        await Assertions.Expect(second.GetByTestId("subject-table-cell").Nth(1)).ToHaveTextAsync("600");
    }

    [Fact]
    public async Task WhenThePageIsEdited_ThenTheLinksOfATableLeadNowhereAndTakeNoFocus()
    {
        // Arrange
        var page = await OpenPageAsync();
        var link = GetTableRow(GetTable(page, "Target"), "Display Motor").GetByTestId("subject-table-link");
        await Assertions.Expect(link).ToHaveAttributeAsync("href", "/browser/Demo/Displays.md/dispmotor", new() { Timeout = Timeout });

        // Act
        await page.SwitchToInlineModeAsync();

        // Assert
        await Assertions.Expect(link).Not.ToHaveAttributeAsync("href", new Regex(".*"), new() { Timeout = Timeout });
        await Assertions.Expect(link).ToHaveAttributeAsync("tabindex", "-1");
    }

    [Fact]
    public async Task WhenFirstColumnHasAMap_ThenItsLinkShowsTheMappedTextInTheColorOfTheCase()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert: the motor reaches one of these states on its own, so the test accepts any of the mapped cases.
        var table = GetTable(page, "State");
        var links = table.GetByTestId("subject-table-link");
        await Assertions.Expect(links).ToHaveCountAsync(2, new() { Timeout = Timeout });

        foreach (var index in new[] { 0, 1 })
        {
            var link = links.Nth(index);
            await Assertions.Expect(link.GetByTestId("value-text")).ToHaveTextAsync(new Regex("^(stopped|starting|running|stopping)$"), new() { Timeout = Timeout });
            await Assertions.Expect(link.GetByTestId("value-text")).ToHaveClassAsync(new Regex(@"\bmud-(error|warning|success)-text\b"));
            await Assertions.Expect(link.GetByTestId("value-missing")).ToHaveCountAsync(0);
        }
    }

    [Fact]
    public async Task WhenFirstColumnValueIsMissing_ThenItsLinkShowsTheRowTitleInstead()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var table = GetTable(page, "Gap");
        var first = GetTableRow(table, "Display Motor").GetByTestId("subject-table-link");
        await Assertions.Expect(first).ToHaveTextAsync("Display Motor", new() { Timeout = Timeout });
        await Assertions.Expect(first.GetByTestId("value-text")).ToHaveCountAsync(0);
        await Assertions.Expect(first.GetByTestId("value-missing")).ToHaveCountAsync(0);

        var second = GetTableRow(table, "Second Motor").GetByTestId("subject-table-link");
        await Assertions.Expect(second).ToHaveTextAsync("Second Motor");
    }

    [Theory]
    [InlineData(400, "Phone")]
    [InlineData(800, "Wide", "Tablet")]
    [InlineData(1100, "Wide", "Tablet", "Large tablet")]
    [InlineData(1400, "Wide", "Desktop")]
    public async Task WhenWindowHasAWidth_ThenOnlyTheContentBetweenItsBreakpointsIsVisibleAndAllIsRendered(int width, params string[] visibleScreens)
    {
        // Arrange
        var page = await OpenPageAsync();

        // Act
        await page.SetViewportSizeAsync(width, 800);

        // Assert
        foreach (var screen in ResponsiveScreens)
        {
            var text = page.GetByText($"{screen} screen text", new() { Exact = true });
            await Assertions.Expect(text).ToHaveCountAsync(1, new() { Timeout = Timeout });
            if (visibleScreens.Contains(screen))
            {
                await Assertions.Expect(text).ToBeVisibleAsync();
            }
            else
            {
                await Assertions.Expect(text).ToBeHiddenAsync();
            }
        }
    }
}
