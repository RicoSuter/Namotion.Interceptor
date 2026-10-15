using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Blazor;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

public class HistoryChartQueryTests
{
    private static readonly DateTimeOffset Origin = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double minutes) => Origin.AddMinutes(minutes);

    private static TestHistoryStore CreateStore() => new(new HistoryCoverage(At(-60), At(120)));

    [Fact]
    public async Task WhenSeriesUseTwoAggregations_ThenEachIsQueriedWithItsOwnAndTheResultKeepsTheRequestOrder()
    {
        // Arrange
        var store = CreateStore();
        HistoryChartSeriesRequest[] requests =
        [
            new("/pump/CurrentSpeed", "Pump", typeof(int), IsBand: false, HistoryAggregations.TimeWeightedAverage),
            new("/pump/Status", "Status", typeof(bool), IsBand: true, HistoryAggregations.Last),
            new("/fan/CurrentSpeed", "Fan", typeof(int), IsBand: false, HistoryAggregations.TimeWeightedAverage)
        ];

        // Act
        var result = await HistoryChartQuery.QueryAsync([store], requests, At(0), At(60), bucket: null, CancellationToken.None);

        // Assert
        var received = store.ReceivedQueries
            .Select(query => (query.PropertyPath, query.Aggregation))
            .OrderBy(entry => entry.PropertyPath, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                ("/fan/CurrentSpeed", HistoryAggregations.TimeWeightedAverage),
                ("/pump/CurrentSpeed", HistoryAggregations.TimeWeightedAverage),
                ("/pump/Status", HistoryAggregations.Last)
            ],
            received);
        Assert.Equal(
            ["/pump/CurrentSpeed", "/pump/Status", "/fan/CurrentSpeed"],
            result.Series.Select(series => series.PropertyPath));
    }

    [Fact]
    public async Task WhenABandIsQueriedRaw_ThenTheValueHeldBeforeTheWindowStartsIt()
    {
        // Arrange
        var store = CreateStore()
            .Add("/pump/IsOn", At(-10), 1)
            .Add("/pump/IsOn", At(30), 0);
        var request = new HistoryChartSeriesRequest("/pump/IsOn", "On", typeof(bool), IsBand: true, HistoryAggregations.Last);

        // Act
        var result = await HistoryChartQuery.QueryAsync([store], [request], At(0), At(60), bucket: null, CancellationToken.None);

        // Assert
        var band = Assert.Single(result.Data.Bands);
        Assert.Equal(["Yes", "No"], band.Segments.Select(segment => segment.Label));
        Assert.Equal(At(30), band.Segments[1].Start);
        Assert.StartsWith("linear-gradient(to right, #2979FF 0%", band.Background);
        Assert.Empty(result.Data.Lines);
        Assert.Equal(
            [new HistoryChartLegendEntry("Yes", "#2979FF"), new HistoryChartLegendEntry("No", "#9E9E9E")],
            result.Data.StateLegend);
    }

    [Fact]
    public async Task WhenLinesHaveNumbers_ThenEachGetsItsRunsAndItsColor()
    {
        // Arrange
        var store = CreateStore()
            .Add("/pump/CurrentSpeed", At(10), 100)
            .Add("/pump/CurrentSpeed", At(20), 200)
            .Add("/fan/CurrentSpeed", At(15), 50);
        HistoryChartSeriesRequest[] requests =
        [
            new("/pump/CurrentSpeed", "Pump", typeof(int), IsBand: false, HistoryAggregations.Last),
            new("/fan/CurrentSpeed", "Fan", typeof(int), IsBand: false, HistoryAggregations.Last, Color: "#123456")
        ];

        // Act
        var result = await HistoryChartQuery.QueryAsync([store], requests, At(0), At(60), bucket: null, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Data.Lines.Count);
        var pump = result.Data.Lines[0];
        Assert.Equal("Pump", pump.Label);
        Assert.Equal("#2979FF", pump.Color);
        Assert.Equal([100d, 200d], Assert.Single(pump.Runs).Select(point => point.Number!.Value));
        var fan = result.Data.Lines[1];
        Assert.Equal("#123456", fan.Color);
        Assert.Equal(2, Assert.Single(fan.Runs).Count);
        Assert.False(result.Data.IsEmpty);
        Assert.False(result.IsTruncated);
    }

    [Theory]
    [InlineData(HistoryAggregations.TimeWeightedAverage)]
    [InlineData(HistoryAggregations.SampleAverage)]
    [InlineData(HistoryAggregations.Minimum)]
    [InlineData(HistoryAggregations.Maximum)]
    [InlineData(HistoryAggregations.Sum)]
    [InlineData(HistoryAggregations.StandardDeviation)]
    [InlineData(HistoryAggregations.First)]
    [InlineData(HistoryAggregations.Last)]
    public async Task WhenALineIsInPercent_ThenItIsDrawnInPercentFromItsRecordedFractions(string aggregation)
    {
        // Arrange
        var store = CreateStore()
            .Add("/host/CpuUsage", At(10), 0.23)
            .Add("/host/CpuUsage", At(20), 0.57);
        var request = new HistoryChartSeriesRequest(
            "/host/CpuUsage", "CPU", typeof(double), IsBand: false, aggregation, Unit: StateUnit.Percent);

        // Act
        var result = await HistoryChartQuery.QueryAsync([store], [request], At(0), At(60), TimeSpan.FromMinutes(10), CancellationToken.None);

        // Assert
        var line = Assert.Single(result.Data.Lines);
        Assert.True(line.IsPercent);
        Assert.Equal([0.23, 0.57], Assert.Single(line.Runs).Select(point => point.Number!.Value));
    }

    [Fact]
    public async Task WhenAPercentLineCountsSamples_ThenItIsNotDrawnInPercent()
    {
        // Arrange
        var store = CreateStore()
            .Add("/host/CpuUsage", At(10), 3)
            .Add("/host/CpuUsage", At(20), 5);
        var request = new HistoryChartSeriesRequest(
            "/host/CpuUsage", "CPU", typeof(double), IsBand: false, HistoryAggregations.Count, Unit: StateUnit.Percent);

        // Act
        var result = await HistoryChartQuery.QueryAsync([store], [request], At(0), At(60), TimeSpan.FromMinutes(10), CancellationToken.None);

        // Assert
        var line = Assert.Single(result.Data.Lines);
        Assert.False(line.IsPercent);
        Assert.Equal([3d, 5d], Assert.Single(line.Runs).Select(point => point.Number!.Value));
    }

    [Fact]
    public async Task WhenAPercentLineIsQueriedRaw_ThenItIsDrawnInPercentWhateverTheAggregation()
    {
        // Arrange
        var store = CreateStore()
            .Add("/host/CpuUsage", At(10), 0.23)
            .Add("/host/CpuUsage", At(20), 1.2);
        var request = new HistoryChartSeriesRequest(
            "/host/CpuUsage", "CPU", typeof(double), IsBand: false, HistoryAggregations.Count, Unit: StateUnit.Percent);

        // Act
        var result = await HistoryChartQuery.QueryAsync([store], [request], At(0), At(60), bucket: null, CancellationToken.None);

        // Assert
        var line = Assert.Single(result.Data.Lines);
        Assert.True(line.IsPercent);
        Assert.Equal([0.23, 1.2], Assert.Single(line.Runs).Select(point => point.Number!.Value));
    }

    [Fact]
    public async Task WhenALineHasAnotherUnit_ThenItIsNotDrawnInPercent()
    {
        // Arrange
        var store = CreateStore()
            .Add("/pump/Power", At(10), 0.23)
            .Add("/pump/Power", At(20), 0.5);
        var request = new HistoryChartSeriesRequest(
            "/pump/Power", "Power", typeof(double), IsBand: false, HistoryAggregations.Last, Unit: StateUnit.Kilowatt);

        // Act
        var result = await HistoryChartQuery.QueryAsync([store], [request], At(0), At(60), bucket: null, CancellationToken.None);

        // Assert
        var line = Assert.Single(result.Data.Lines);
        Assert.False(line.IsPercent);
        Assert.Equal([0.23, 0.5], Assert.Single(line.Runs).Select(point => point.Number!.Value));
    }

    [Fact]
    public async Task WhenALineHasNoNumbers_ThenItHasNoRunsAndTheChartIsEmpty()
    {
        // Arrange
        var store = CreateStore();
        var request = new HistoryChartSeriesRequest("/pump/CurrentSpeed", "Pump", typeof(int), IsBand: false, HistoryAggregations.Last);

        // Act
        var result = await HistoryChartQuery.QueryAsync([store], [request], At(0), At(60), bucket: null, CancellationToken.None);

        // Assert
        Assert.Empty(Assert.Single(result.Data.Lines).Runs);
        Assert.True(result.Data.IsEmpty);
    }

    [Fact]
    public async Task WhenAStoreTruncates_ThenTheResultIsTruncated()
    {
        // Arrange
        var store = CreateStore().Add("/pump/CurrentSpeed", At(10), 100);
        store.IsTruncated = true;
        var request = new HistoryChartSeriesRequest("/pump/CurrentSpeed", "Pump", typeof(int), IsBand: false, HistoryAggregations.Last);

        // Act
        var result = await HistoryChartQuery.QueryAsync([store], [request], At(0), At(60), bucket: null, CancellationToken.None);

        // Assert
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public async Task WhenALineHasANullSample_ThenItBreaksIntoTwoRuns()
    {
        // Arrange
        var store = CreateStore()
            .Add("/pump/CurrentSpeed", At(10), 100)
            .Add("/pump/CurrentSpeed", At(20), 200)
            .Add("/pump/CurrentSpeed", At(30), null)
            .Add("/pump/CurrentSpeed", At(40), 300)
            .Add("/pump/CurrentSpeed", At(50), 400);
        var request = new HistoryChartSeriesRequest("/pump/CurrentSpeed", "Pump", typeof(int), IsBand: false, HistoryAggregations.Last);

        // Act
        var result = await HistoryChartQuery.QueryAsync([store], [request], At(0), At(60), bucket: null, CancellationToken.None);

        // Assert
        var runs = Assert.Single(result.Data.Lines).Runs;
        Assert.Equal(2, runs.Count);
        Assert.Equal([100d, 200d], runs[0].Select(point => point.Number!.Value));
        Assert.Equal([300d, 400d], runs[1].Select(point => point.Number!.Value));
    }

    [Fact]
    public async Task WhenTwoStoresCoverTheWindow_ThenLinesAndBandsMergeBothAndTheBandLooksBackIntoTheOlderStore()
    {
        // Arrange
        var old = new TestHistoryStore(new HistoryCoverage(At(-60), At(30)), priority: 0)
            .Add("/pump/IsOn", At(-10), 1)
            .Add("/pump/CurrentSpeed", At(10), 100);
        var recent = new TestHistoryStore(new HistoryCoverage(At(30), At(120)), priority: 1)
            .Add("/pump/IsOn", At(40), 0)
            .Add("/pump/CurrentSpeed", At(40), 200);
        HistoryChartSeriesRequest[] requests =
        [
            new("/pump/CurrentSpeed", "Pump", typeof(int), IsBand: false, HistoryAggregations.Last),
            new("/pump/IsOn", "On", typeof(bool), IsBand: true, HistoryAggregations.Last)
        ];

        // Act
        var result = await HistoryChartQuery.QueryAsync([recent, old], requests, At(0), At(60), bucket: null, CancellationToken.None);

        // Assert
        var points = Assert.Single(result.Data.Lines).Runs.SelectMany(run => run).Select(point => point.Number!.Value).ToArray();
        Assert.Contains(100d, points);
        Assert.Contains(200d, points);
        var band = Assert.Single(result.Data.Bands);
        Assert.Equal(["Yes", "No"], band.Segments.Select(segment => segment.Label));
        Assert.Equal(At(0), band.Segments[0].Start);
        Assert.Equal(At(40), band.Segments[1].Start);
        Assert.All(band.Segments, segment => Assert.False(segment.IsUnknown));
    }

    [Fact]
    public async Task WhenAChartIsBucketed_ThenItsBandIsQueriedRawAndTheValueHeldBeforeTheWindowStartsIt()
    {
        // Arrange
        var bucket = TimeSpan.FromMinutes(10);
        var store = CreateStore()
            .Add("/pump/IsOn", At(-10), 1)
            .Add("/pump/IsOn", At(30), 0)
            .Add("/pump/CurrentSpeed", At(10), 100);
        HistoryChartSeriesRequest[] requests =
        [
            new("/pump/CurrentSpeed", "Pump", typeof(int), IsBand: false, HistoryAggregations.Last),
            new("/pump/IsOn", "On", typeof(bool), IsBand: true, HistoryAggregations.Last)
        ];

        // Act
        var result = await HistoryChartQuery.QueryAsync([store], requests, At(0), At(60), bucket, CancellationToken.None);

        // Assert
        var queries = store.ReceivedQueries;
        Assert.Equal(bucket, Assert.Single(queries, query => query.PropertyPath == "/pump/CurrentSpeed").Bucket);
        Assert.Null(Assert.Single(queries, query => query.PropertyPath == "/pump/IsOn").Bucket);
        Assert.Equal(bucket, result.Data.Bucket);
        var band = Assert.Single(result.Data.Bands);
        Assert.Collection(
            band.Segments,
            held => Assert.Equal((At(0), At(30), "Yes", false), (held.Start, held.End, held.Label, held.IsUnknown)),
            changed => Assert.Equal((At(30), At(60), "No", false), (changed.Start, changed.End, changed.Label, changed.IsUnknown)));
    }

    [Fact]
    public async Task WhenABandAsksForAnotherAggregationInABucketedChart_ThenItIsStillQueriedRawAsLast()
    {
        // Arrange
        var store = CreateStore().Add("/pump/IsOn", At(10), 1);
        var request = new HistoryChartSeriesRequest("/pump/IsOn", "On", typeof(bool), IsBand: true, HistoryAggregations.First);

        // Act
        await HistoryChartQuery.QueryAsync([store], [request], At(0), At(60), TimeSpan.FromMinutes(10), CancellationToken.None);

        // Assert
        var query = Assert.Single(store.ReceivedQueries);
        Assert.Null(query.Bucket);
        Assert.Equal(HistoryAggregations.Last, query.Aggregation);
    }

    [Fact]
    public async Task WhenTheTokenIsCancelled_ThenTheQueryIsCancelled()
    {
        // Arrange
        var store = CreateStore().Add("/pump/CurrentSpeed", At(10), 100);
        var request = new HistoryChartSeriesRequest("/pump/CurrentSpeed", "Pump", typeof(int), IsBand: false, HistoryAggregations.Last);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HistoryChartQuery.QueryAsync([store], [request], At(0), At(60), bucket: null, cancellation.Token));
    }

    [Fact]
    public async Task WhenAnAggregationIsNotSupported_ThenTheQueryThrows()
    {
        // Arrange
        var store = CreateStore();
        var request = new HistoryChartSeriesRequest("/pump/CurrentSpeed", "Pump", typeof(int), IsBand: false, "Unknown");

        // Act & Assert
        await Assert.ThrowsAsync<HistoryAggregationNotSupportedException>(
            () => HistoryChartQuery.QueryAsync([store], [request], At(0), At(60), TimeSpan.FromMinutes(10), CancellationToken.None));
    }

    [Fact]
    public void WhenStoresCoverHistory_ThenAvailableCoverageRunsFromTheEarliestCoveredInstant()
    {
        // Arrange
        var recent = new TestHistoryStore(new HistoryCoverage(At(-30), At(60)));
        var old = new TestHistoryStore(new HistoryCoverage(At(-90), At(-60)));

        // Act
        var coverage = HistoryChartQuery.GetAvailableCoverage([recent, old], At(60));

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(150), coverage);
    }

    [Fact]
    public void WhenNoStoreCoversAnythingBeforeTheEnd_ThenAvailableCoverageIsNull()
    {
        // Arrange
        var later = new TestHistoryStore(new HistoryCoverage(At(70), At(80)));

        // Act
        var withLaterStore = HistoryChartQuery.GetAvailableCoverage([later], At(60));
        var withoutStore = HistoryChartQuery.GetAvailableCoverage([], At(60));

        // Assert
        Assert.Null(withLaterStore);
        Assert.Null(withoutStore);
    }
}
