using System.Globalization;
using System.Text.Json;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.E2E.Tests.Models;
using HomeBlaze.History;
using HomeBlaze.History.Abstractions;
using HomeBlaze.Samples;
using HomeBlaze.Services;
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

    private static readonly TimeSpan RecordingTimeout = TimeSpan.FromSeconds(20);

    private readonly PlaywrightFixture _fixture;

    public HistoryChartWidgetTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    // The chart queries once after its first render and then only after its refresh window of a minute, so the
    // page is opened only when the store of the host holds a recorded speed of the motor at the path the chart queries.
    private async Task WaitUntilSpeedIsRecordedAsync()
    {
        var motor = (Motor)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children[MotorName];
        var resolver = _fixture.ServerServices.GetRequiredService<SubjectPathResolver>();
        var speed = motor.TryGetRegisteredProperty(nameof(Motor.CurrentSpeed))!;
        var historyPath = speed.GetHistoryPath(resolver)
            ?? throw new InvalidOperationException("The chart motor is not in the graph");
        var registry = _fixture.ServerServices.GetRequiredService<IInterceptorSubjectContext>().GetService<ISubjectRegistry>();

        // The condition is synchronous, so a poll starts a lookup and a later poll reads its result.
        Task<HistoryPoint?>? lookup = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                if (lookup is { IsCompletedSuccessfully: true } && lookup.Result?.Number is not null)
                {
                    return true;
                }

                if (lookup is null || lookup.IsCompleted)
                {
                    lookup = registry.GetValueAtAsync(historyPath, DateTimeOffset.UtcNow, CancellationToken.None);
                }

                return false;
            },
            RecordingTimeout,
            message: $"The history store recorded no value of {historyPath}");
    }

    private async Task<IPage> OpenPageAsync(int width)
    {
        await WaitUntilSpeedIsRecordedAsync();
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(width, 900);
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        return page;
    }

    private sealed record ChartLayout(LocatorBoundingBoxResult Widget, LocatorBoundingBoxResult Lines, LocatorBoundingBoxResult Chart, int ValueLabelCount);

    // MudChart renders more than one svg, so the first stands for the chart.
    private static async Task<ChartLayout> GetLayoutAsync(IPage page)
    {
        var widget = page.Locator("[data-testid='history-chart']").First;
        var lines = widget.Locator("[data-testid='history-chart-lines']");
        var chart = lines.Locator("svg").First;
        await Assertions.Expect(chart).ToBeVisibleAsync(new() { Timeout = Timeout });

        // The chart is drawn at a default size first and fits itself to its box once it has measured the box, and the
        // widget spaces the labels once it has measured the lines.
        await page.WaitForFunctionAsync(
            """
            () => {
                const lines = document.querySelector("[data-testid='history-chart'] [data-testid='history-chart-lines']");
                const chart = lines?.querySelector("svg");
                const box = lines?.getBoundingClientRect();
                return chart && Math.abs(chart.getBoundingClientRect().height - box.height) <= 1 &&
                    chart.getAttribute("viewBox") === `0 0 ${Math.round(box.width)} ${Math.round(box.height)}`;
            }
            """,
            null,
            new() { Timeout = Timeout });

        return new ChartLayout(
            await widget.BoundingBoxAsync() ?? throw new InvalidOperationException("The widget has no box"),
            await lines.BoundingBoxAsync() ?? throw new InvalidOperationException("The lines have no box"),
            await chart.BoundingBoxAsync() ?? throw new InvalidOperationException("The chart has no box"),
            await chart.Locator(".mud-charts-yaxis text").CountAsync());
    }

    [Fact]
    public async Task WhenScreenIsWide_ThenTheLinesFillTheWidthAtTheirHeightWithAFewValueLabels()
    {
        // Arrange & Act
        var page = await OpenPageAsync(DesktopWidth);
        var layout = await GetLayoutAsync(page);

        // Assert
        Assert.InRange(layout.Lines.Width, layout.Widget.Width - 2, layout.Widget.Width + 2);
        Assert.InRange(layout.Lines.Height, DefaultHeight - 1, DefaultHeight + 1);
        Assert.InRange(layout.Chart.Width, layout.Lines.Width - 1, layout.Lines.Width + 1);
        Assert.InRange(layout.Chart.Height, layout.Lines.Height - 1, layout.Lines.Height + 1);
        Assert.InRange(layout.ValueLabelCount, 3, 7);
    }

    [Fact]
    public async Task WhenScreenIsAsNarrowAsAPhone_ThenTheLinesFillTheWidthAndGetLowerWithFewerValueLabels()
    {
        // Arrange & Act
        var page = await OpenPageAsync(PhoneWidth);
        var layout = await GetLayoutAsync(page);

        // Assert
        Assert.InRange(layout.Lines.Width, layout.Widget.Width - 2, layout.Widget.Width + 2);
        Assert.True(layout.Lines.Height < DefaultHeight - 50, $"The lines are {layout.Lines.Height} px high on a phone");
        Assert.InRange(layout.Lines.Height, layout.Lines.Width / 2 - 2, layout.Lines.Width / 2 + 2);
        Assert.InRange(layout.Chart.Height, layout.Lines.Height - 1, layout.Lines.Height + 1);
        Assert.InRange(layout.ValueLabelCount, 2, 4);
    }

    // Each value is written once the one before it is recorded, because the store keeps only the last of the values
    // written within its buffer time. Null is a value recorded as empty, which breaks a line.
    private static readonly (string Property, Action<HistoryValuesSubject, int> Write, double?[] Values)[] RecordedValues =
    [
        (nameof(HistoryValuesSubject.Constant), (subject, index) => subject.Constant = Values(nameof(HistoryValuesSubject.Constant))[index], [1200]),
        (nameof(HistoryValuesSubject.Single), (subject, index) => subject.Single = Values(nameof(HistoryValuesSubject.Single))[index], [0.37]),
        (nameof(HistoryValuesSubject.CrossingZero), (subject, index) => subject.CrossingZero = Values(nameof(HistoryValuesSubject.CrossingZero))[index], [-15, 5, 25]),
        (nameof(HistoryValuesSubject.Negative), (subject, index) => subject.Negative = Values(nameof(HistoryValuesSubject.Negative))[index], [-80, -20, -50]),
        (nameof(HistoryValuesSubject.Large), (subject, index) => subject.Large = Values(nameof(HistoryValuesSubject.Large))[index], [1_000_000, 3_500_000, 2_000_000]),
        (nameof(HistoryValuesSubject.Huge), (subject, index) => subject.Huge = Values(nameof(HistoryValuesSubject.Huge))[index], [1e12, 5e12]),
        (nameof(HistoryValuesSubject.Small), (subject, index) => subject.Small = Values(nameof(HistoryValuesSubject.Small))[index], [0.5, 0.501, 0.5005]),
        (nameof(HistoryValuesSubject.Decimal), (subject, index) => subject.Decimal = Values(nameof(HistoryValuesSubject.Decimal))[index], [40.25, 46.75, 43.5]),
        (nameof(HistoryValuesSubject.Gapped), (subject, index) => subject.Gapped = Values(nameof(HistoryValuesSubject.Gapped))[index], [10, null, 20, 30]),
        (nameof(HistoryValuesSubject.Fraction), (subject, index) => subject.Fraction = Values(nameof(HistoryValuesSubject.Fraction))[index], [0.2, 0.9, 0.4]),
        (nameof(HistoryValuesSubject.IsOn), (subject, index) => subject.IsOn = Values(nameof(HistoryValuesSubject.IsOn))[index] == 1, [1, 0, 1]),
    ];

    private static readonly string[] LineChartLabels =
        ["Constant", "Single", "Crossing zero", "Negative", "Large", "Huge", "Small", "Decimal", "Gapped", "Fraction", "Bucketed"];

    private static double?[] Values(string property) => RecordedValues.First(entry => entry.Property == property).Values;

    private async Task RecordValuesAsync(HistoryValuesSubject subject)
    {
        var resolver = _fixture.ServerServices.GetRequiredService<SubjectPathResolver>();
        var registry = _fixture.ServerServices.GetRequiredService<IInterceptorSubjectContext>().GetService<ISubjectRegistry>();
        var valueCount = RecordedValues.Max(entry => entry.Values.Length);
        for (var index = 0; index < valueCount; index++)
        {
            var written = RecordedValues.Where(entry => index < entry.Values.Length).ToArray();
            foreach (var entry in written)
            {
                entry.Write(subject, index);
            }

            foreach (var entry in written)
            {
                var path = subject.TryGetRegisteredProperty(entry.Property)!.GetHistoryPath(resolver)
                    ?? throw new InvalidOperationException("The values subject is not in the graph");
                var expected = entry.Values[index];

                // The condition is synchronous, so a poll starts a lookup and a later poll reads its result.
                Task<HistoryPoint?>? lookup = null;
                await AsyncTestHelpers.WaitUntilAsync(
                    () =>
                    {
                        if (lookup is { IsCompletedSuccessfully: true, Result: { } point } && point.Number == expected)
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
                    message: $"The history store recorded no {expected} at {path}");
            }
        }
    }

    [Theory]
    [InlineData(PhoneWidth)]
    [InlineData(DesktopWidth)]
    public async Task WhenValuesAreConstantNegativeLargeSmallOrBroken_ThenEveryAxisHasFewDistinctFiniteLabelsAndHoldsItsLines(int width)
    {
        // Arrange
        var folder = _fixture.GetDemoFolder();
        var originalChildren = folder.Children;
        var subject = _fixture.ServerServices.GetRequiredService<SubjectFactory>().CreateSubject<HistoryValuesSubject>();

        try
        {
            folder.Children = new Dictionary<string, IInterceptorSubject>(originalChildren) { [ValuesSubjectName] = subject };
            await RecordValuesAsync(subject);

            // Act
            var page = await _fixture.CreatePageAsync();
            await page.SetViewportSizeAsync(width, 900);
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ValuesPageName}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            // Assert
            foreach (var label in LineChartLabels)
            {
                var chart = page.GetByTestId("history-chart").Filter(new()
                {
                    Has = page.GetByTestId("history-chart-legend-entry").GetByText(label, new() { Exact = true })
                });
                var lines = chart.GetByTestId("history-chart-lines");
                var svg = lines.Locator("svg").First;
                await Assertions.Expect(svg).ToBeVisibleAsync(new() { Timeout = Timeout });

                // The labels are read once the chart is drawn at the size of its box.
                var result = await lines.EvaluateAsync<JsonElement>(
                    """
                    async lines => {
                        const svg = lines.querySelector("svg");
                        for (let attempt = 0; attempt < 300; attempt++) {
                            const box = lines.getBoundingClientRect();
                            if (svg.getAttribute("viewBox") === `0 0 ${Math.round(box.width)} ${Math.round(box.height)}` &&
                                svg.querySelector(".mud-charts-timeseries-series path")) {
                                break;
                            }

                            await new Promise(resolve => requestAnimationFrame(resolve));
                        }

                        const height = svg.viewBox.baseVal.height;
                        const paths = [...svg.querySelectorAll(".mud-charts-timeseries-series path")].map(path => path.getBBox());
                        return {
                            values: [...svg.querySelectorAll(".mud-charts-yaxis text")].map(text => text.textContent),
                            times: [...svg.querySelectorAll(".mud-charts-xaxis text")].map(text => text.textContent),
                            lineTop: Math.min(...paths.map(box => box.y)),
                            lineBottom: Math.max(...paths.map(box => box.y + box.height)),
                            height
                        };
                    }
                    """);
                var axis = new ChartAxis(
                    result.GetProperty("values").EnumerateArray().Select(text => text.GetString()!).ToArray(),
                    result.GetProperty("times").EnumerateArray().Select(text => text.GetString()!).ToArray(),
                    result.GetProperty("lineTop").GetDouble(),
                    result.GetProperty("lineBottom").GetDouble(),
                    result.GetProperty("height").GetDouble());

                Assert.InRange(axis.Values.Length, 2, HistoryChartAxisLabelLimit(width));
                Assert.Equal(axis.Values.Length, axis.Values.Distinct().Count());
                Assert.All(axis.Values, text => Assert.True(
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value),
                    $"{label}: the value label '{text}' is no finite number"));
                Assert.All(axis.Times, text => Assert.DoesNotContain("NaN", text));
                Assert.Equal(axis.Times.Length, axis.Times.Distinct().Count());
                Assert.True(axis.LineTop >= 0 && axis.LineBottom <= axis.Height,
                    $"{label}: the lines reach from {axis.LineTop} to {axis.LineBottom} in a chart {axis.Height} high");
            }

            var bands = page.GetByTestId("history-chart").Filter(new()
            {
                Has = page.GetByTestId("history-chart-legend-entry").GetByText("Only a band", new() { Exact = true })
            });
            await Assertions.Expect(bands.GetByTestId("history-chart-band")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(bands.GetByTestId("history-chart-lines")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByTestId("widget-argument-error")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByTestId("history-chart-query-error")).ToHaveCountAsync(0);
        }
        finally
        {
            folder.Children = originalChildren;
        }
    }

    // The lines of a phone are about 184 pixels high, which holds three labels, and of a desktop 300, which holds five.
    private static int HistoryChartAxisLabelLimit(int width) => width == PhoneWidth ? 3 : 5;

    private sealed record ChartAxis(string[] Values, string[] Times, double LineTop, double LineBottom, double Height);
}
