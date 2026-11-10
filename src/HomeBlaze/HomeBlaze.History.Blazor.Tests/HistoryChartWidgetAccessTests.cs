using System.Text.RegularExpressions;
using Bunit;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.History.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Authorization.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

public sealed partial class HistoryChartWidgetAccessTests : BunitContext, IAsyncLifetime
{
    private const string MissingPath = "/Nothing";
    private const string TemperaturePath = "/" + nameof(ChartAccessRoot.Temperature);
    private const string PressurePath = "/" + nameof(ChartAccessRoot.Pressure);

    // The default of one second is too short for the queries of the chart while other tests load the machine.
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly TestUserPermissions _permissions = new();
    private readonly ChartAccessRoot _root;

    public HistoryChartWidgetAccessTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);

        var context = SubjectContextFactory.Create(new ServiceCollection());
        _root = new ChartAccessRoot(context) { Temperature = 21, Pressure = 2 };
        var resolver = new SubjectPathResolver(() => _root);

        Services.AddSingleton(context);
        Services.AddSingleton(resolver);
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<IUserPermissions>(_permissions);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenASeriesIsBoundToAPropertyTheUserMayNotRead_ThenTheChartRendersAsForAPathThatLeadsNowhere(bool refusesHistoryOnly)
    {
        // Arrange
        var temperature = GetProperty(nameof(ChartAccessRoot.Temperature));
        (refusesHistoryOnly ? _permissions.CannotReadHistory : _permissions.CannotRead).Add(temperature);

        // Act
        var hidden = Normalize(RenderChartWithoutStore(TemperaturePath), TemperaturePath);
        var missing = Normalize(RenderChartWithoutStore(MissingPath), MissingPath);

        // Assert
        Assert.Equal(missing, hidden);
    }

    [Fact]
    public void WhenASeriesIsBoundToAPropertyTheUserMayRead_ThenTheChartRendersOtherwiseThanForAPathThatLeadsNowhere()
    {
        // Act
        var readable = Normalize(RenderChartWithoutStore(TemperaturePath), TemperaturePath);
        var missing = Normalize(RenderChartWithoutStore(MissingPath), MissingPath);

        // Assert
        Assert.NotEqual(missing, readable);
        Assert.Contains("Water temperature", readable);
    }

    [Fact]
    public void WhenTheUserLosesReadAccess_ThenTheShownDataIsDroppedAtOnceAndTheSeriesIsMissing()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var history = new TestHistoryStore(new HistoryCoverage(now.AddDays(-2), now.AddDays(1)))
            .Add(TemperaturePath, now.AddHours(-2), 20)
            .Add(TemperaturePath, now.AddHours(-1), 22)
            .Add(PressurePath, now.AddHours(-2), 1)
            .Add(PressurePath, now.AddHours(-1), 3);
        var store = new ChartStoreSubject(history);
        _root.Store = store;
        var chart = RenderChart(TemperaturePath, PressurePath);
        chart.WaitForAssertion(() => Assert.Null(chart.Find("[data-testid=history-chart-view]").GetAttribute("aria-busy")), WaitTimeout);

        // Act
        store.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _permissions.CannotRead.Add(GetProperty(nameof(ChartAccessRoot.Temperature)));
        _permissions.RaiseChanged();

        // Assert
        try
        {
            // The query for the new access waits at the gate, so the view can only show the placeholder.
            chart.WaitForAssertion(() =>
            {
                Assert.Equal("true", chart.Find("[data-testid=history-chart-view]").GetAttribute("aria-busy"));
                Assert.DoesNotContain("Water temperature", chart.Markup);
                Assert.Contains("Pressure", chart.Markup);
            }, WaitTimeout);
        }
        finally
        {
            store.Gate.SetResult();
        }
    }

    private IRenderedComponent<HistoryChartWidget> RenderChart(params string[] paths)
    {
        var series = new HistoryChartSeries[paths.Length];
        for (var index = 0; index < paths.Length; index++)
        {
            series[index] = new HistoryChartSeries { Path = paths[index] };
        }

        var chart = new HistoryChart { Series = series };
        _root.Chart = chart;
        return Render<HistoryChartWidget>(parameters => parameters.Add(widget => widget.Subject, chart));
    }

    // Waits for the first query, which finds no store and replaces the placeholder with the legend of every series.
    private string RenderChartWithoutStore(string path)
    {
        var chart = RenderChart(path);

        // Until the widget knows that there is no store, it renders the view with the legend inside it.
        chart.WaitForAssertion(() =>
        {
            Assert.Empty(chart.FindAll("[data-testid=history-chart-view]"));
            Assert.NotEmpty(chart.FindAll("[data-testid=history-chart-legend]"));
        }, WaitTimeout);
        return chart.Markup;
    }

    private RegisteredSubjectProperty GetProperty(string name) => _root.TryGetRegisteredSubject()!.TryGetProperty(name)!;

    // The path shows where a series is missing, and the identifiers the renderer generates differ from render to render.
    // Like the normalization of WidgetReadAccessTests in HomeBlaze.Components.Tests, which this project does not reference.
    private static string Normalize(string markup, string path) =>
        GeneratedIdentifier().Replace(markup.Replace(path, "{path}", StringComparison.Ordinal), "$1=\"\"");

    [GeneratedRegex("(?<=\\s)(id|blazor:[A-Za-z]+)=\"[^\"]*\"")]
    private static partial Regex GeneratedIdentifier();
}
