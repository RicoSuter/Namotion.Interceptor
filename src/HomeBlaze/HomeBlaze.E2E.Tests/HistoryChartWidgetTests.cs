using System.Globalization;
using System.Text.Json;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.E2E.Tests.Models;
using HomeBlaze.History;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Blazor;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the history chart widget on a markdown page.
/// </summary>
/// <remarks>
/// The query, the bands, the units and the refresh window are covered by the unit tests of
/// HomeBlaze.History.Blazor. What only a browser shows is that the block finds its widget, that the
/// widget reaches the history store of the host after its first interactive render, and how it lays out
/// what it got.
/// </remarks>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class HistoryChartWidgetTests
{
    private const int Timeout = 30000;
    private const string PageName = "HistoryChart.md";
    private const string MotorName = "chartmotor";
    private const string ValuesPageName = "HistoryChartValues.md";
    private const string ValuesSubjectName = "HistoryValues";
    private const int DefaultHeight = 300;
    private const int PhoneWidth = 400;
    private const int DesktopWidth = 1400;
    private const string MissingText = "–";

    private static readonly TimeSpan RecordingTimeout = TimeSpan.FromSeconds(20);

    private readonly PlaywrightFixture _fixture;

    public HistoryChartWidgetTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    // The chart queries once after its first render and then only after its refresh window of a minute, so the
    // page is opened only when the store of the host holds a recorded speed of the motor at the path the chart queries.
    private async Task<IPage> OpenPageAsync(int width)
    {
        var motor = (Motor)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children[MotorName];
        await WaitUntilRecordedAsync(motor.TryGetRegisteredProperty(nameof(Motor.CurrentSpeed))!, point => point.Number is not null);
        return await OpenAsync(PageName, width);
    }

    private async Task<IPage> OpenAsync(string pageName, int width)
    {
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(width, 900);
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{pageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        return page;
    }

    private async Task WaitUntilRecordedAsync(RegisteredSubjectProperty property, Func<HistoryPoint, bool> isRecorded)
    {
        var resolver = _fixture.ServerServices.GetRequiredService<SubjectPathResolver>();
        var path = property.GetHistoryPath(resolver) ?? throw new InvalidOperationException("The subject is not in the graph");
        var registry = _fixture.ServerServices.GetRequiredService<IInterceptorSubjectContext>().GetService<ISubjectRegistry>();

        // The condition is synchronous, so a poll starts a lookup and a later poll reads its result.
        Task<HistoryPoint?>? lookup = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                if (lookup is { IsCompletedSuccessfully: true, Result: { } point } && isRecorded(point))
                {
                    return true;
                }

                if (lookup is null || lookup.IsCompleted)
                {
                    lookup = registry.GetValueAtAsync(path, DateTimeOffset.UtcNow, CancellationToken.None);
                }

                return false;
            },
            RecordingTimeout,
            message: $"The history store did not record the expected value of {path}");
    }

    private static ILocator GetChart(IPage page, string legendLabel) =>
        page.GetByTestId("history-chart").Filter(new()
        {
            Has = page.GetByTestId("history-chart-legend-entry").GetByText(legendLabel, new() { Exact = true })
        });

    private sealed record ChartLayout(
        LocatorBoundingBoxResult Widget,
        LocatorBoundingBoxResult Lines,
        LocatorBoundingBoxResult Chart,
        string[] ValueLabels,
        string[] TimeLabels,
        double LineTop,
        double LineBottom);

    // Waits until the chart is drawn at the size of its box with the axes spaced for that size, then reads it. MudChart
    // draws at a default size first and fits itself to the box once it has measured it, and the view spaces the axes
    // once it has measured the lines. The first svg stands for the chart, because MudChart renders more than one.
    private static async Task<ChartLayout> GetLayoutAsync(ILocator widget)
    {
        var lines = widget.GetByTestId("history-chart-lines");
        var chart = lines.Locator("svg").First;
        await Assertions.Expect(chart).ToBeVisibleAsync(new() { Timeout = Timeout });

        // The label counts follow the rule of HistoryChartAxis and are computed from the live box, so a box that changes
        // while the chart settles, for example when a scrollbar appears, is waited for rather than timed out on.
        await widget.Page.WaitForFunctionAsync(
            """
            ([lines, valueLabelPixels, timeLabelPixels, maximumTimeLabels]) => {
                const chart = lines.querySelector("svg");
                const box = lines.getBoundingClientRect();
                const valueLabels = box.height >= 2 * valueLabelPixels ? Math.floor(box.height / valueLabelPixels) : 2;
                const timeLabels = Math.floor(Math.min(Math.max(box.width / timeLabelPixels, 2), maximumTimeLabels));
                return lines.getAttribute("data-axes-labels") === `${valueLabels}x${timeLabels}` &&
                    chart.getAttribute("viewBox") === `0 0 ${Math.round(box.width)} ${Math.round(box.height)}` &&
                    chart.querySelector(".mud-charts-timeseries-series path") !== null;
            }
            """,
            new object[]
            {
                await lines.ElementHandleAsync(),
                HistoryChartAxis.PixelsPerValueLabel,
                HistoryChartAxis.PixelsPerTimeLabel,
                HistoryChartAxis.MaximumTimeLabelCount
            },
            new() { Timeout = Timeout });

        var box = await lines.BoundingBoxAsync() ?? throw new InvalidOperationException("The lines have no box");

        var drawn = await lines.EvaluateAsync<JsonElement>(
            """
            lines => {
                const chart = lines.querySelector("svg");
                const paths = [...chart.querySelectorAll(".mud-charts-timeseries-series path")].map(path => path.getBBox());
                return {
                    values: [...chart.querySelectorAll(".mud-charts-yaxis text")].map(text => text.textContent),
                    times: [...chart.querySelectorAll(".mud-charts-xaxis text")].map(text => text.textContent),
                    top: Math.min(...paths.map(path => path.y)),
                    bottom: Math.max(...paths.map(path => path.y + path.height))
                };
            }
            """);

        return new ChartLayout(
            await widget.BoundingBoxAsync() ?? throw new InvalidOperationException("The widget has no box"),
            box,
            await chart.BoundingBoxAsync() ?? throw new InvalidOperationException("The chart has no box"),
            drawn.GetProperty("values").EnumerateArray().Select(text => text.GetString()!).ToArray(),
            drawn.GetProperty("times").EnumerateArray().Select(text => text.GetString()!).ToArray(),
            drawn.GetProperty("top").GetDouble(),
            drawn.GetProperty("bottom").GetDouble());
    }

    [Fact]
    public async Task WhenAPageHasAHistoryChart_ThenItDrawsTheLineTheBandAndTheCurrentValues()
    {
        // Arrange & Act
        var page = await OpenPageAsync(DesktopWidth);
        var chart = GetChart(page, "Chart speed");

        // Assert
        // MudChart renders more than one svg, so the first stands for the chart.
        await Assertions.Expect(chart.GetByTestId("history-chart-lines").Locator("svg").First)
            .ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(chart.GetByTestId("history-chart-band"))
            .ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(chart.GetByTestId("history-chart-legend-entry"))
            .ToHaveCountAsync(2, new() { Timeout = Timeout });
        await Assertions.Expect(chart.GetByTestId("history-chart-legend-value").First)
            .Not.ToHaveTextAsync(MissingText, new() { Timeout = Timeout });
        await Assertions.Expect(chart.GetByTestId("history-chart-legend-note")).ToHaveCountAsync(0);
        await Assertions.Expect(chart.GetByTestId("history-chart-query-error")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAChartMixesUnits_ThenItShowsAnErrorThatNamesTheSeries()
    {
        // Arrange & Act
        var page = await OpenPageAsync(DesktopWidth);
        var error = page.GetByTestId("widget-argument-error");

        // Assert
        await Assertions.Expect(error).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await Assertions.Expect(error).ToContainTextAsync("Mixed temperature");
    }

    [Fact]
    public async Task WhenScreenIsWide_ThenTheLinesFillTheWidthAtTheirHeightWithAFewValueLabels()
    {
        // Arrange & Act
        var page = await OpenPageAsync(DesktopWidth);
        var layout = await GetLayoutAsync(GetChart(page, "Chart speed"));

        // Assert
        Assert.InRange(layout.Lines.Width, layout.Widget.Width - 2, layout.Widget.Width + 2);
        Assert.InRange(layout.Lines.Height, DefaultHeight - 1, DefaultHeight + 1);
        Assert.InRange(layout.Chart.Width, layout.Lines.Width - 1, layout.Lines.Width + 1);
        Assert.InRange(layout.Chart.Height, layout.Lines.Height - 1, layout.Lines.Height + 1);
        Assert.InRange(layout.ValueLabels.Length, 3, 7);
    }

    [Fact]
    public async Task WhenScreenIsAsNarrowAsAPhone_ThenTheLinesFillTheWidthAndGetLowerWithFewerValueLabels()
    {
        // Arrange & Act
        var page = await OpenPageAsync(PhoneWidth);
        var layout = await GetLayoutAsync(GetChart(page, "Chart speed"));

        // Assert
        Assert.InRange(layout.Lines.Width, layout.Widget.Width - 2, layout.Widget.Width + 2);
        Assert.True(layout.Lines.Height < DefaultHeight - 50, $"The lines are {layout.Lines.Height} px high on a phone");
        Assert.InRange(layout.Lines.Height, layout.Lines.Width / 2 - 2, layout.Lines.Width / 2 + 2);
        Assert.InRange(layout.Chart.Height, layout.Lines.Height - 1, layout.Lines.Height + 1);
        Assert.InRange(layout.ValueLabels.Length, 2, 4);
    }

    [Theory]
    [InlineData(PhoneWidth)]
    [InlineData(DesktopWidth)]
    public async Task WhenAChartIsInATabThatIsHiddenAtFirst_ThenItIsDrawnInsideItsBoxWhenTheTabIsShown(int width)
    {
        // Arrange
        const int height = 200;
        var page = await OpenPageAsync(width);
        var chart = GetChart(page, "Tab speed");

        // The chart has drawn its lines box while its tab is hidden.
        await Assertions.Expect(chart.GetByTestId("history-chart-lines")).ToBeAttachedAsync(new() { Timeout = Timeout });
        await Assertions.Expect(chart).ToBeHiddenAsync();

        // Act
        await page.GetByTestId("tab-layout").GetByRole(AriaRole.Tab, new() { Name = "Speed" }).ClickAsync();
        var layout = await GetLayoutAsync(chart);
        var legend = await chart.GetByTestId("history-chart-legend").BoundingBoxAsync()
            ?? throw new InvalidOperationException("The legend has no box");

        // Assert
        var expectedHeight = Math.Min(height, layout.Lines.Width / 2);
        Assert.InRange(layout.Lines.Height, expectedHeight - 2, expectedHeight + 2);
        Assert.InRange(layout.Chart.Height, layout.Lines.Height - 1, layout.Lines.Height + 1);
        Assert.InRange(layout.Chart.Y + layout.Chart.Height, layout.Lines.Y, layout.Lines.Y + layout.Lines.Height + 1);
        Assert.True(layout.Chart.Y + layout.Chart.Height <= legend.Y + 1,
            $"The chart ends at {layout.Chart.Y + layout.Chart.Height} px, below the top of the legend at {legend.Y} px");
    }

    [Theory]
    [InlineData(PhoneWidth)]
    [InlineData(DesktopWidth)]
    public async Task WhenAChartHasLinesAndABand_ThenTheLineLegendIsAboveTheBandAndTheBandSpansThePlotArea(int width)
    {
        // Arrange
        var page = await OpenPageAsync(width);
        var chart = GetChart(page, "Chart speed");
        await GetLayoutAsync(chart);
        var band = chart.GetByTestId("history-chart-band");
        await Assertions.Expect(band).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act - MudChart widens the space left of the plot once it has measured its value labels, and the band follows
        // the plot area it drew, so the edges are read once they agree.
        var edges = await page.WaitForFunctionAsync(
            """
            ([lines, band]) => {
                const gridLine = [...lines.querySelectorAll(".mud-charts-grid path")]
                    .map(path => path.getBoundingClientRect())
                    .find(box => box.height < 1 && box.width > 0);
                const bandBox = band.getBoundingClientRect();
                return gridLine && Math.abs(bandBox.left - gridLine.left) <= 2 && Math.abs(bandBox.right - gridLine.right) <= 2
                    ? { bandLeft: bandBox.left, bandRight: bandBox.right, plotLeft: gridLine.left, plotRight: gridLine.right }
                    : null;
            }
            """,
            new object[] { await chart.GetByTestId("history-chart-lines").ElementHandleAsync(), await band.ElementHandleAsync() },
            new() { Timeout = Timeout });
        var aligned = await edges.JsonValueAsync<JsonElement>();
        var legend = await chart.GetByTestId("history-chart-legend").BoundingBoxAsync()
            ?? throw new InvalidOperationException("The legend has no box");
        var header = await chart.GetByTestId("history-chart-band-header").First.BoundingBoxAsync()
            ?? throw new InvalidOperationException("The band header has no box");
        var lines = await chart.GetByTestId("history-chart-lines").BoundingBoxAsync()
            ?? throw new InvalidOperationException("The lines have no box");

        // Assert
        var plotLeft = aligned.GetProperty("plotLeft").GetDouble();
        Assert.True(plotLeft > lines.X + 10, $"The plot starts at {plotLeft} px, at the left edge of the lines at {lines.X} px");
        Assert.InRange(aligned.GetProperty("bandLeft").GetDouble(), plotLeft - 2, plotLeft + 2);
        Assert.True(legend.Y >= lines.Y + lines.Height - 1,
            $"The legend starts at {legend.Y} px, above the bottom of the lines at {lines.Y + lines.Height} px");
        Assert.True(legend.Y + legend.Height <= header.Y + 1,
            $"The legend ends at {legend.Y + legend.Height} px, below the top of the band header at {header.Y} px");
        await Assertions.Expect(chart.GetByTestId("history-chart-band-header").GetByTestId("history-chart-legend-entry"))
            .ToContainTextAsync("Chart status");
        await Assertions.Expect(chart.GetByTestId("history-chart-legend").GetByText("Chart status")).ToHaveCountAsync(0);
        await Assertions.Expect(chart.GetByTestId("history-chart-range")).ToHaveCountAsync(0);
    }

    // Written one value after the other, each once the one before it is recorded, because the store keeps only the last
    // of the values written within its buffer time. Null is a value recorded as empty, which breaks a line, and the
    // boolean is written as 1 for true.
    private static readonly (string Property, double?[] Values)[] RecordedValues =
    [
        (nameof(HistoryValuesSubject.Constant), [1200]),
        (nameof(HistoryValuesSubject.Single), [0.37]),
        (nameof(HistoryValuesSubject.CrossingZero), [-15, 5, 25]),
        (nameof(HistoryValuesSubject.Negative), [-80, -20, -50]),
        (nameof(HistoryValuesSubject.Large), [1_000_000, 3_500_000, 2_000_000]),
        (nameof(HistoryValuesSubject.Huge), [1e12, 5e12]),
        (nameof(HistoryValuesSubject.Small), [0.5, 0.501, 0.5005]),
        (nameof(HistoryValuesSubject.Decimal), [40.25, 46.75, 43.5]),
        (nameof(HistoryValuesSubject.Gapped), [10, null, 20, 30]),
        (nameof(HistoryValuesSubject.Fraction), [0.2, 0.9, 0.4]),
        (nameof(HistoryValuesSubject.Percent), [0.2, 0.9, 0.4]),
        (nameof(HistoryValuesSubject.IsOn), [1, 0, 1]),
    ];

    private static readonly string[] LineChartLabels =
        ["Constant", "Single", "Crossing zero", "Negative", "Large", "Huge", "Small", "Decimal", "Gapped", "Fraction", "Percent", "Two scales", "Bucketed"];

    // Values below one need a step of one, the smallest the chart takes; its default of 20 pinned them to the floor.
    private static readonly string[] ChartsBelowOne = ["Single", "Small", "Fraction"];

    private async Task RecordValuesAsync(HistoryValuesSubject subject)
    {
        // A sample of an earlier run is at the same path, so only one written after this run started counts.
        var start = DateTimeOffset.UtcNow;
        var valueCount = RecordedValues.Max(entry => entry.Values.Length);
        for (var index = 0; index < valueCount; index++)
        {
            var written = RecordedValues.Where(entry => index < entry.Values.Length).ToArray();
            foreach (var (property, values) in written)
            {
                var value = values[index];
                subject.TryGetRegisteredProperty(property)!.SetValue(
                    property == nameof(HistoryValuesSubject.IsOn) ? value == 1 : value);
            }

            foreach (var (property, values) in written)
            {
                var expected = values[index];
                await WaitUntilRecordedAsync(
                    subject.TryGetRegisteredProperty(property)!,
                    point => point.Timestamp >= start && point.Number == expected);
            }
        }
    }

    [Theory]
    [InlineData(PhoneWidth)]
    [InlineData(DesktopWidth)]
    public async Task WhenValuesAreConstantNegativeLargeSmallOrBroken_ThenEveryAxisHasFewDistinctFiniteLabelsAndHoldsItsLines(int width)
    {
        // Arrange
        var root = (FluentStorageContainer)_fixture.ServerServices.GetRequiredService<RootManager>().Root!;
        var subjectFactory = _fixture.ServerServices.GetRequiredService<SubjectFactory>();
        var subject = subjectFactory.CreateSubject<HistoryValuesSubject>();

        // A pass of the storage that finds a change sets the children of the folder anew from its files, which drops
        // the subject, so the changes that earlier tests left on disk are taken in first.
        await root.ReconcileAsync();
        var folder = _fixture.GetDemoFolder();
        var originalChildren = folder.Children;

        try
        {
            folder.Children = new Dictionary<string, IInterceptorSubject>(originalChildren) { [ValuesSubjectName] = subject };
            await RecordValuesAsync(subject);

            // Act
            var page = await OpenAsync(ValuesPageName, width);

            // Assert
            var valueLabelLimit = width == PhoneWidth ? 3 : 5;
            foreach (var label in LineChartLabels)
            {
                var layout = await GetLayoutAsync(GetChart(page, label));
                var height = layout.Lines.Height;

                Assert.InRange(layout.ValueLabels.Length, 2, valueLabelLimit);
                Assert.Equal(layout.ValueLabels.Length, layout.ValueLabels.Distinct().Count());
                Assert.All(layout.ValueLabels, text => Assert.True(
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value),
                    $"{label}: the value label '{text}' is no finite number"));
                Assert.All(layout.TimeLabels, text => Assert.DoesNotContain("NaN", text));
                Assert.Equal(layout.TimeLabels.Length, layout.TimeLabels.Distinct().Count());
                Assert.True(layout.LineTop >= 0 && layout.LineBottom <= height,
                    $"{label}: the lines reach from {layout.LineTop} to {layout.LineBottom} in a chart {height} high");

                if (ChartsBelowOne.Contains(label))
                {
                    Assert.Equal("1", layout.ValueLabels[^1]);
                }

                // A fraction in percent is drawn times 100 on an axis from 0 to 100.
                if (label == "Percent")
                {
                    Assert.Equal(["0", "100"], [layout.ValueLabels[0], layout.ValueLabels[^1]]);
                }

                if (label == "Fraction")
                {
                    Assert.True(layout.LineBottom - layout.LineTop >= 0.3 * height,
                        $"Fraction: 0.2 to 0.9 takes {layout.LineBottom - layout.LineTop} of {height} pixels");
                }
            }

            var bands = GetChart(page, "Only a band");
            await Assertions.Expect(bands.GetByTestId("history-chart-band")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(bands.GetByTestId("history-chart-lines")).ToHaveCountAsync(0);
            await Assertions.Expect(bands.GetByTestId("history-chart-range")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("widget-argument-error")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByTestId("history-chart-query-error")).ToHaveCountAsync(0);
        }
        finally
        {
            folder.Children = originalChildren;
            subjectFactory.DiscardSubject(subject);
        }
    }
}
