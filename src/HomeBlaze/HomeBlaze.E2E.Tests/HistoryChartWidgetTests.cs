using HomeBlaze.E2E.Tests.Infrastructure;
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

    // Returns the box of the lines and of the chart drawn in it. MudChart renders more than one svg, so the first stands for the chart.
    private static async Task<(LocatorBoundingBoxResult Lines, LocatorBoundingBoxResult Chart)> GetLinesBoxAsync(IPage page)
    {
        var lines = page.Locator("[data-testid='history-chart']").First.Locator("[data-testid='history-chart-lines']");
        var chart = lines.Locator("svg").First;
        await Assertions.Expect(chart).ToBeVisibleAsync(new() { Timeout = Timeout });

        // The chart is drawn at a default size first and fits itself to its box once it has measured the box.
        await page.WaitForFunctionAsync(
            """
            () => {
                const lines = document.querySelector("[data-testid='history-chart'] [data-testid='history-chart-lines']");
                const chart = lines?.querySelector("svg");
                return chart && chart.getBoundingClientRect().height <= lines.getBoundingClientRect().height + 1;
            }
            """,
            null,
            new() { Timeout = Timeout });

        return (
            await lines.BoundingBoxAsync() ?? throw new InvalidOperationException("The lines have no box"),
            await chart.BoundingBoxAsync() ?? throw new InvalidOperationException("The chart has no box"));
    }

    [Fact]
    public async Task WhenScreenIsWide_ThenTheLinesHaveTheirHeight()
    {
        // Arrange & Act
        var page = await OpenPageAsync(DesktopWidth);
        var (box, chart) = await GetLinesBoxAsync(page);

        // Assert
        Assert.InRange(box.Height, DefaultHeight - 1, DefaultHeight + 1);
        Assert.InRange(chart.Height, 1, box.Height + 1);
    }

    [Fact]
    public async Task WhenScreenIsAsNarrowAsAPhone_ThenTheLinesGetLowerAndKeepTheirProportions()
    {
        // Arrange & Act
        var page = await OpenPageAsync(PhoneWidth);
        var (box, chart) = await GetLinesBoxAsync(page);

        // Assert
        Assert.True(box.Height < DefaultHeight - 50, $"The lines are {box.Height} px high on a phone");
        Assert.InRange(box.Height, box.Width / 2 - 2, box.Width / 2 + 2);
        Assert.InRange(chart.Height, 1, box.Height + 1);
    }
}
