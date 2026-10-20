using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.InMemory;
using HomeBlaze.History.Sqlite;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

/// <summary>
/// A refresh of a bar chart reuses the bars of its previous result that cannot have changed, and queries only from the
/// bucket that held the end of what the stores had committed. It must draw what a full query draws.
/// </summary>
public sealed class HistoryChartQueryReuseTests : IDisposable
{
    private const string Imported = "/meter/TotalImportedEnergy";
    private const string Exported = "/meter/TotalExportedEnergy";

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
    private static readonly HistoryBucket Bucket = HistoryBucket.Calendar(HistoryCalendarUnit.Day, Zone);

    private static readonly HistoryChartSeriesRequest[] Requests =
    [
        new(Imported, "Imported", typeof(double), IsBand: false, HistoryAggregations.Delta),
        new(Exported, "Exported", typeof(double), IsBand: false, HistoryAggregations.Delta, IsNegated: true)
    ];

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-chart-reuse-" + Guid.NewGuid().ToString("N"));

    private readonly List<IDisposable> _disposables = [];
    private DateTimeOffset _now = Day(0);

    public static TheoryData<string> StoreKinds => new() { "InMemory", "Sqlite" };

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task WhenABarChartIsRefreshed_ThenItQueriesFromTheNewestDayAndDrawsTheBarsOfAFullQuery(string storeKind)
    {
        // Arrange
        var (store, recorder, flush) = CreateStore(storeKind);
        var recording = new QueryRecordingStore(store);
        RecordCounters(recorder, Day(0), Day(4).AddHours(10));
        _now = Day(4).AddHours(10);
        await flush();
        var previous = await HistoryChartQuery.QueryAsync([recording], Requests, Day(1), _now, Bucket, CancellationToken.None);
        RecordCounters(recorder, Day(4).AddHours(10), Day(4).AddHours(11));
        _now = Day(4).AddHours(11);
        await flush();
        recording.Clear();

        // Act
        var reused = await HistoryChartQuery.QueryAsync([recording], Requests, Day(1), _now, Bucket, previous, CancellationToken.None);
        var full = await HistoryChartQuery.QueryAsync([store], Requests, Day(1), _now, Bucket, CancellationToken.None);

        // Assert
        Assert.All(recording.ReceivedQueries, query => Assert.Equal(Day(4), query.From));
        AssertSameBars(full, reused);
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task WhenTheDayChangesBetweenRefreshes_ThenTheEndedDayIsQueriedOnceMoreAndTheWindowMoves(string storeKind)
    {
        // Arrange
        var (store, recorder, flush) = CreateStore(storeKind);
        var recording = new QueryRecordingStore(store);
        RecordCounters(recorder, Day(0), Day(4).AddMinutes(-30));
        _now = Day(4).AddMinutes(-30);
        await flush();
        var previous = await HistoryChartQuery.QueryAsync([recording], Requests, Day(1), _now, Bucket, CancellationToken.None);
        RecordCounters(recorder, Day(4).AddMinutes(-30), Day(4).AddMinutes(30));
        _now = Day(4).AddMinutes(30);
        await flush();
        recording.Clear();

        // Act
        var reused = await HistoryChartQuery.QueryAsync([recording], Requests, Day(2), _now, Bucket, previous, CancellationToken.None);
        var full = await HistoryChartQuery.QueryAsync([store], Requests, Day(2), _now, Bucket, CancellationToken.None);

        // Assert
        Assert.All(recording.ReceivedQueries, query => Assert.Equal(Day(3), query.From));
        Assert.Equal([Day(2), Day(3), Day(4)], reused.Series[0].Points.Select(point => point.Timestamp));
        AssertSameBars(full, reused);
    }

    [Fact]
    public async Task WhenAReadingIsFlushedAfterThePreviousQuery_ThenItsDayIsQueriedAgain()
    {
        // Arrange - the last reading of day 3 is still pending when the first query of day 4 runs, so the store
        // covers day 3 only up to it then.
        var (store, recorder, flush) = CreateStore("Sqlite");
        var recording = new QueryRecordingStore(store);
        RecordCounters(recorder, Day(0), Day(4).AddMinutes(-15));
        _now = Day(4).AddMinutes(-15);
        await flush();
        RecordCounters(recorder, Day(4).AddMinutes(-15), Day(4));
        _now = Day(4).AddMinutes(5);
        var previous = await HistoryChartQuery.QueryAsync([recording], Requests, Day(1), _now, Bucket, CancellationToken.None);
        RecordCounters(recorder, Day(4), Day(4).AddMinutes(30));
        _now = Day(4).AddMinutes(30);
        await flush();
        recording.Clear();

        // Act
        var reused = await HistoryChartQuery.QueryAsync([recording], Requests, Day(1), _now, Bucket, previous, CancellationToken.None);
        var full = await HistoryChartQuery.QueryAsync([store], Requests, Day(1), _now, Bucket, CancellationToken.None);

        // Assert
        Assert.All(recording.ReceivedQueries, query => Assert.Equal(Day(3), query.From));
        AssertSameBars(full, reused);
    }

    [Fact]
    public async Task WhenTheSeriesChange_ThenEverythingIsQueried()
    {
        // Arrange
        var store = CreateTestStore();
        var previous = await HistoryChartQuery.QueryAsync([store], Requests, Day(1), Day(3), Bucket, CancellationToken.None);
        HistoryChartSeriesRequest[] requests = [Requests[0]];

        // Act
        await HistoryChartQuery.QueryAsync([store], requests, Day(1), Day(3).AddHours(1), Bucket, previous, CancellationToken.None);

        // Assert
        Assert.Equal(Day(1), store.ReceivedQueries[^1].From);
    }

    [Fact]
    public async Task WhenTheTimeZoneChanges_ThenEverythingIsQueried()
    {
        // Arrange
        var store = CreateTestStore();
        var previous = await HistoryChartQuery.QueryAsync([store], Requests, Day(1), Day(3), Bucket, CancellationToken.None);
        var bucket = HistoryBucket.Calendar(HistoryCalendarUnit.Day, TimeZoneInfo.Utc);
        var from = BucketAlignment.Start(Day(1), bucket);

        // Act
        await HistoryChartQuery.QueryAsync([store], Requests, from, Day(3).AddHours(1), bucket, previous, CancellationToken.None);

        // Assert
        Assert.All(store.ReceivedQueries.Skip(Requests.Length), query => Assert.Equal(from, query.From));
    }

    [Fact]
    public async Task WhenTheCoverageOfTheKeptDaysChanged_ThenEverythingIsQueried()
    {
        // Arrange
        var store = CreateTestStore();
        var previous = await HistoryChartQuery.QueryAsync([store], Requests, Day(1), Day(3), Bucket, CancellationToken.None);
        store.CoverageRanges = [new HistoryCoverage(Day(1).AddHours(6), Day(10))];

        // Act
        await HistoryChartQuery.QueryAsync([store], Requests, Day(1), Day(3).AddHours(1), Bucket, previous, CancellationToken.None);

        // Assert
        Assert.All(store.ReceivedQueries.Skip(Requests.Length), query => Assert.Equal(Day(1), query.From));
    }

    [Fact]
    public async Task WhenTheStoresChange_ThenEverythingIsQueried()
    {
        // Arrange
        var store = CreateTestStore();
        var previous = await HistoryChartQuery.QueryAsync([store], Requests, Day(1), Day(3), Bucket, CancellationToken.None);
        var added = CreateTestStore();

        // Act
        await HistoryChartQuery.QueryAsync([store, added], Requests, Day(1), Day(3).AddHours(1), Bucket, previous, CancellationToken.None);

        // Assert
        Assert.All(store.ReceivedQueries.Skip(Requests.Length), query => Assert.Equal(Day(1), query.From));
    }

    [Fact]
    public async Task WhenTheWindowStartsEarlier_ThenEverythingIsQueried()
    {
        // Arrange
        var store = CreateTestStore();
        var previous = await HistoryChartQuery.QueryAsync([store], Requests, Day(1), Day(3), Bucket, CancellationToken.None);

        // Act
        await HistoryChartQuery.QueryAsync([store], Requests, Day(0), Day(3).AddHours(1), Bucket, previous, CancellationToken.None);

        // Assert
        Assert.All(store.ReceivedQueries.Skip(Requests.Length), query => Assert.Equal(Day(0), query.From));
    }

    [Fact]
    public async Task WhenAPreviousSeriesWasTruncated_ThenEverythingIsQueried()
    {
        // Arrange
        var store = CreateTestStore();
        store.IsTruncated = true;
        var previous = await HistoryChartQuery.QueryAsync([store], Requests, Day(1), Day(3), Bucket, CancellationToken.None);

        // Act
        await HistoryChartQuery.QueryAsync([store], Requests, Day(1), Day(3).AddHours(1), Bucket, previous, CancellationToken.None);

        // Assert
        Assert.All(store.ReceivedQueries.Skip(Requests.Length), query => Assert.Equal(Day(1), query.From));
    }

    [Fact]
    public async Task WhenTheChartDrawsLines_ThenEverythingIsQueried()
    {
        // Arrange
        var store = CreateTestStore();
        HistoryChartSeriesRequest[] requests =
            [new(Imported, "Imported", typeof(double), IsBand: false, HistoryAggregations.Maximum)];
        var previous = await HistoryChartQuery.QueryAsync([store], requests, Day(1), Day(3), Bucket, CancellationToken.None);

        // Act
        await HistoryChartQuery.QueryAsync([store], requests, Day(1), Day(3).AddHours(1), Bucket, previous, CancellationToken.None);

        // Assert
        Assert.Equal(Day(1), store.ReceivedQueries[^1].From);
    }

    [Fact]
    public async Task WhenABarChartHasABand_ThenTheBandIsQueriedOverTheWholeWindow()
    {
        // Arrange
        var store = CreateTestStore();
        HistoryChartSeriesRequest[] requests =
        [
            Requests[0],
            new("/meter/Status", "Status", typeof(bool), IsBand: true, HistoryAggregations.Last)
        ];
        var previous = await HistoryChartQuery.QueryAsync([store], requests, Day(1), Day(3), Bucket, CancellationToken.None);

        // Act
        await HistoryChartQuery.QueryAsync([store], requests, Day(1), Day(3).AddHours(1), Bucket, previous, CancellationToken.None);

        // Assert
        var refreshed = store.ReceivedQueries.Skip(requests.Length).ToDictionary(query => query.PropertyPath, query => query.From);
        Assert.Equal(Day(3), refreshed[Imported]);
        Assert.Equal(Day(1), refreshed["/meter/Status"]);
    }

    private static DateTimeOffset Day(int day) => BucketAlignment.LocalTimeToInstant(new DateTime(2026, 6, 1).AddDays(day), Zone);

    // Readings every 15 minutes in [from, to): the import counter restarts at noon of day 2.
    private static void RecordCounters(IHistoryRecorder recorder, DateTimeOffset from, DateTimeOffset to)
    {
        var restart = Day(2).AddHours(12);
        for (var timestamp = from; timestamp < to; timestamp = timestamp.AddMinutes(15))
        {
            var quarters = (timestamp - Day(0)).Ticks / TimeSpan.FromMinutes(15).Ticks;
            var imported = timestamp < restart
                ? 1_000 + quarters * 0.25
                : (timestamp - restart).Ticks / TimeSpan.FromMinutes(15).Ticks * 0.25;
            recorder.TryRecord(Imported, timestamp, imported, typeof(double));
            recorder.TryRecord(Exported, timestamp, 500 + quarters * 0.1, typeof(double));
        }
    }

    private (IHistoryStore Store, IHistoryRecorder Recorder, Func<Task> Flush) CreateStore(string storeKind)
    {
        // The stores read the clock when they are created to begin their coverage there.
        _now = Day(0);
        if (storeKind == "Sqlite")
        {
            var sqlite = new SqliteHistoryStore(
                priority: 50, _directory, PartitionInterval.Weekly, TimeSpan.FromDays(3650), maxJsonSize: 8192, () => _now);
            _disposables.Add(sqlite);
            return (sqlite, sqlite, () => sqlite.FlushAsync(CancellationToken.None));
        }

        var inMemory = new InMemoryHistoryStore(
            priority: 100, maxPointsPerProperty: 100_000, TimeSpan.FromDays(3650), maxJsonSize: 8192, () => _now);
        return (inMemory, inMemory, () => Task.CompletedTask);
    }

    private static TestHistoryStore CreateTestStore() =>
        new TestHistoryStore(new HistoryCoverage(Day(0), Day(10)))
            .Add(Imported, Day(1), 1)
            .Add(Imported, Day(2), 2)
            .Add(Exported, Day(1), 3);

    private static void AssertSameBars(HistoryChartResult full, HistoryChartResult reused)
    {
        Assert.Equal(full.Series.Count, reused.Series.Count);
        for (var index = 0; index < full.Series.Count; index++)
        {
            Assert.NotEmpty(full.Series[index].Points);
            Assert.Equal(full.Series[index].Points.AsEnumerable(), reused.Series[index].Points.AsEnumerable());
            Assert.Equal(full.Series[index].CoverageRanges.AsEnumerable(), reused.Series[index].CoverageRanges.AsEnumerable());
            Assert.Equal(
                full.Data.Lines[index].Runs.SelectMany(run => run),
                reused.Data.Lines[index].Runs.SelectMany(run => run));
        }
    }

    /// <summary>Passes every call to another store and records the queries it got.</summary>
    private sealed class QueryRecordingStore(IHistoryStore inner) : IHistoryStore
    {
        private readonly List<HistoryQuery> _receivedQueries = [];

        public int Priority => inner.Priority;

        public ImmutableArray<HistoryCoverage> CoverageRanges => inner.CoverageRanges;

        public IReadOnlySet<string> SupportedAggregations => inner.SupportedAggregations;

        public IReadOnlyList<HistoryQuery> ReceivedQueries
        {
            get
            {
                lock (_receivedQueries)
                {
                    return _receivedQueries.ToArray();
                }
            }
        }

        public void Clear()
        {
            lock (_receivedQueries)
            {
                _receivedQueries.Clear();
            }
        }

        public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
        {
            // The merger fans a batch out concurrently.
            lock (_receivedQueries)
            {
                _receivedQueries.Add(query);
            }

            return inner.QueryAsync(query, cancellationToken);
        }

        public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
            string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken) =>
            inner.GetSampleAtOrBeforeAsync(propertyPath, asOf, cancellationToken);

        public ValueTask<HistoryPoint?> GetLastNumericSampleBeforeAsync(
            string propertyPath, DateTimeOffset before, CancellationToken cancellationToken) =>
            inner.GetLastNumericSampleBeforeAsync(propertyPath, before, cancellationToken);
    }
}
