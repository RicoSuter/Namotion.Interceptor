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
    public async Task WhenAStoreServingAnEndedDayHasNotFlushed_ThenThatDayIsQueriedAgainAlthoughAnotherStoreCoversUpToNow()
    {
        // Arrange - the memory store covers only the last minute, so the persistent store serves day 3, whose last
        // reading it still holds pending when the previous query runs.
        _now = Day(0);
        var inMemory = new InMemoryHistoryStore(
            priority: 100, maxPointsPerProperty: 100_000, TimeSpan.FromMinutes(1), maxJsonSize: 8192, () => _now);
        var sqlite = new SqliteHistoryStore(
            priority: 50, _directory, PartitionInterval.Weekly, TimeSpan.FromDays(3650), maxJsonSize: 8192, () => _now);
        _disposables.Add(sqlite);
        var recording = new QueryRecordingStore(sqlite);
        IHistoryStore[] stores = [inMemory, recording];
        RecordCounters(inMemory, Day(0), Day(4).AddMinutes(-15));
        RecordCounters(sqlite, Day(0), Day(4).AddMinutes(-15));
        _now = Day(4).AddMinutes(-15);
        await sqlite.FlushAsync(CancellationToken.None);
        RecordCounters(inMemory, Day(4).AddMinutes(-15), Day(4).AddMinutes(30));
        RecordCounters(sqlite, Day(4).AddMinutes(-15), Day(4).AddMinutes(30));
        _now = Day(4).AddMinutes(20);
        var previous = await HistoryChartQuery.QueryAsync(stores, Requests, Day(1), _now, Bucket, CancellationToken.None);
        _now = Day(4).AddMinutes(30);
        await sqlite.FlushAsync(CancellationToken.None);
        recording.Clear();

        // Act
        var reused = await HistoryChartQuery.QueryAsync(stores, Requests, Day(1), _now, Bucket, previous, CancellationToken.None);
        var full = await HistoryChartQuery.QueryAsync([inMemory, sqlite], Requests, Day(1), _now, Bucket, CancellationToken.None);

        // Assert
        Assert.All(recording.ReceivedQueries, query => Assert.Equal(Day(3), query.From));
        AssertSameBars(full, reused);
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task WhenAReadingArrivesLateWithAnEarlierTimestamp_ThenItsDayIsQueriedAgain(string storeKind)
    {
        // Arrange - a reading of 23:59 on day 3 reaches the store only after the query at 00:02 of day 4.
        var (store, recorder, flush) = CreateStore(storeKind);
        var recording = new QueryRecordingStore(store);
        RecordCounters(recorder, Day(0), Day(4).AddMinutes(1));
        _now = Day(4).AddMinutes(2);
        await flush();
        var previous = await HistoryChartQuery.QueryAsync([recording], Requests, Day(1), _now, Bucket, CancellationToken.None);
        RecordReading(recorder, Day(4).AddMinutes(-1));
        RecordCounters(recorder, Day(4).AddMinutes(15), Day(4).AddMinutes(30));
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
    public async Task WhenNothingChanged_ThenTheLinesAreQueriedFromTheNewestDay()
    {
        // Arrange
        var store = CreateTestStore();
        var previous = await QueryFirstAsync(store);

        // Act
        var refreshed = await RefreshAsync(store, previous);

        // Assert
        Assert.All(refreshed, query => Assert.Equal(Day(3), query.From));
    }

    [Fact]
    public async Task WhenTheNewestDayChanges_ThenEverythingIsQueried()
    {
        // Arrange - the window moves with the day, by one day.
        var store = CreateTestStore();
        var previous = await HistoryChartQuery.QueryAsync([store], Requests, Day(1), Day(4).AddMinutes(-30), Bucket, CancellationToken.None);
        var count = store.ReceivedQueries.Count;

        // Act
        await HistoryChartQuery.QueryAsync([store], Requests, Day(2), Day(4).AddMinutes(30), Bucket, previous, CancellationToken.None);

        // Assert
        var refreshed = store.ReceivedQueries.Skip(count).ToArray();
        Assert.NotEmpty(refreshed);
        Assert.All(refreshed, query => Assert.Equal(Day(2), query.From));
    }

    [Fact]
    public async Task WhenTheSeriesChange_ThenEverythingIsQueried()
    {
        // Arrange
        var store = CreateTestStore();
        var previous = await QueryFirstAsync(store);

        // Act
        var refreshed = await RefreshAsync(store, previous, requests: [Requests[0]]);

        // Assert
        Assert.All(refreshed, query => Assert.Equal(Day(1), query.From));
    }

    [Fact]
    public async Task WhenTheTimeZoneChanges_ThenEverythingIsQueried()
    {
        // Arrange - the window of the other zone starts after that of the previous query.
        var store = CreateTestStore();
        var previous = await QueryFirstAsync(store);
        var bucket = HistoryBucket.Calendar(HistoryCalendarUnit.Day, TimeZoneInfo.Utc);
        var from = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero);

        // Act
        var refreshed = await RefreshAsync(store, previous, from: from, bucket: bucket);

        // Assert
        Assert.True(from > previous.Data.From);
        Assert.All(refreshed, query => Assert.Equal(from, query.From));
    }

    [Fact]
    public async Task WhenTheCoverageOfTheKeptDaysChanged_ThenEverythingIsQueried()
    {
        // Arrange
        var store = CreateTestStore();
        var previous = await QueryFirstAsync(store);
        store.CoverageRanges = [new HistoryCoverage(Day(1).AddHours(6), Day(10))];

        // Act
        var refreshed = await RefreshAsync(store, previous);

        // Assert
        Assert.All(refreshed, query => Assert.Equal(Day(1), query.From));
    }

    [Fact]
    public async Task WhenTheStoresChange_ThenEverythingIsQueried()
    {
        // Arrange
        var store = CreateTestStore();
        var previous = await QueryFirstAsync(store);

        // Act
        var refreshed = await RefreshAsync(store, previous, stores: [store, CreateTestStore()]);

        // Assert
        Assert.All(refreshed, query => Assert.Equal(Day(1), query.From));
    }

    [Fact]
    public async Task WhenTheWindowStartsEarlier_ThenEverythingIsQueried()
    {
        // Arrange
        var store = CreateTestStore();
        var previous = await QueryFirstAsync(store);

        // Act
        var refreshed = await RefreshAsync(store, previous, from: Day(0));

        // Assert
        Assert.All(refreshed, query => Assert.Equal(Day(0), query.From));
    }

    [Fact]
    public async Task WhenAPreviousLineWasTruncated_ThenEverythingIsQueried()
    {
        // Arrange
        var store = CreateTestStore();
        store.IsTruncated = true;
        var previous = await QueryFirstAsync(store);

        // Act
        var refreshed = await RefreshAsync(store, previous);

        // Assert
        Assert.All(refreshed, query => Assert.Equal(Day(1), query.From));
    }

    [Fact]
    public async Task WhenTheChartDrawsLines_ThenEverythingIsQueried()
    {
        // Arrange
        var store = CreateTestStore();
        HistoryChartSeriesRequest[] requests =
            [new(Imported, "Imported", typeof(double), IsBand: false, HistoryAggregations.Maximum)];
        var previous = await QueryFirstAsync(store, requests);

        // Act
        var refreshed = await RefreshAsync(store, previous, requests: requests);

        // Assert
        Assert.All(refreshed, query => Assert.Equal(Day(1), query.From));
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
        var previous = await QueryFirstAsync(store, requests);

        // Act
        var refreshed = await RefreshAsync(store, previous, requests: requests);

        // Assert
        var queryFrom = refreshed.ToDictionary(query => query.PropertyPath, query => query.From);
        Assert.Equal(Day(3), queryFrom[Imported]);
        Assert.Equal(Day(1), queryFrom["/meter/Status"]);
    }

    // The first query of a test store, which ends at 01:00 of day 3.
    private static Task<HistoryChartResult> QueryFirstAsync(
        TestHistoryStore store, IReadOnlyList<HistoryChartSeriesRequest>? requests = null) =>
        HistoryChartQuery.QueryAsync([store], requests ?? Requests, Day(1), Day(3).AddHours(1), Bucket, CancellationToken.None);

    // The queries a test store gets for a refresh an hour after the first query.
    private static async Task<HistoryQuery[]> RefreshAsync(
        TestHistoryStore store,
        HistoryChartResult previous,
        IReadOnlyList<HistoryChartSeriesRequest>? requests = null,
        DateTimeOffset? from = null,
        HistoryBucket? bucket = null,
        IReadOnlyList<IHistoryStore>? stores = null)
    {
        var count = store.ReceivedQueries.Count;
        await HistoryChartQuery.QueryAsync(
            stores ?? [store], requests ?? Requests, from ?? Day(1), Day(3).AddHours(2), bucket ?? Bucket, previous, CancellationToken.None);
        var refreshed = store.ReceivedQueries.Skip(count).ToArray();
        Assert.NotEmpty(refreshed);
        return refreshed;
    }

    private static DateTimeOffset Day(int day) => BucketAlignment.LocalTimeToInstant(new DateTime(2026, 6, 1).AddDays(day), Zone);

    // Readings every 15 minutes in [from, to).
    private static void RecordCounters(IHistoryRecorder recorder, DateTimeOffset from, DateTimeOffset to)
    {
        for (var timestamp = from; timestamp < to; timestamp = timestamp.AddMinutes(15))
        {
            RecordReading(recorder, timestamp);
        }
    }

    // The import counter grows by 1 an hour and restarts at noon of day 2, the export counter grows by 0.4 an hour.
    private static void RecordReading(IHistoryRecorder recorder, DateTimeOffset timestamp)
    {
        var restart = Day(2).AddHours(12);
        var imported = timestamp < restart ? 1_000 + (timestamp - Day(0)).TotalHours : (timestamp - restart).TotalHours;
        recorder.TryRecord(Imported, timestamp, imported, typeof(double));
        recorder.TryRecord(Exported, timestamp, 500 + (timestamp - Day(0)).TotalHours * 0.4, typeof(double));
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
