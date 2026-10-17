using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;
using Xunit;

namespace HomeBlaze.History.Parity.Tests;

/// <summary>
/// A SQLite store whose partition interval is made coarser twice while data exists (Daily, then Weekly,
/// then Monthly) holds partition files that overlap in time. Every query must still answer as an in-memory
/// store that recorded the same samples.
/// </summary>
public sealed class PartitionIntervalChangeParityTests : IDisposable
{
    private const string Path = "/a/Energy";

    // A Monday, so the daily and the weekly file of that day share one key.
    private static readonly DateTimeOffset Monday = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = Monday.AddDays(5);
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    private static readonly (PartitionInterval Interval, DateTimeOffset SessionStart, (double Hours, double? Value)[] Samples)[] Sessions =
    [
        (PartitionInterval.Daily, Monday.AddYears(-1), [(3, 100), (15, 104), (33, null), (45, 110)]),
        (PartitionInterval.Weekly, Monday.AddDays(2), [(54, 113), (66, 117)]),
        (PartitionInterval.Monthly, Monday.AddDays(3), [(84, 125), (104, 130)])
    ];

    private readonly string _directory =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hb-parity-interval-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    public static TheoryData<string, string> Cases()
    {
        var cases = new TheoryData<string, string>();
        foreach (var bucket in new[] { "6h", "calendar day" })
        {
            foreach (var aggregation in new[]
                     {
                         HistoryAggregations.Delta, HistoryAggregations.TimeWeightedAverage,
                         HistoryAggregations.Count, HistoryAggregations.Sum, HistoryAggregations.First,
                         HistoryAggregations.Last, HistoryAggregations.Minimum, HistoryAggregations.Maximum,
                         HistoryAggregations.SampleAverage, HistoryAggregations.StandardDeviation
                     })
            {
                cases.Add(bucket, aggregation);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task WhenIntervalIsMadeCoarserTwice_ThenBucketedQueriesMatchInMemory(string bucket, string aggregation)
    {
        // Arrange
        using var inMemory = RecordInMemory();
        using var sqlite = await RecordInSqliteAcrossIntervalChangesAsync();
        var query = new HistoryQuery(
            Path, Monday, To,
            bucket == "6h" ? TimeSpan.FromHours(6) : HistoryBucket.Calendar(HistoryCalendarUnit.Day, Zurich),
            aggregation, MaxPoints: 1000);

        // Act
        var expected = inMemory.Query(query);
        var actual = sqlite.Query(query);

        // Assert
        Assert.Equal(expected.Points.Select(point => point.Timestamp), actual.Points.Select(point => point.Timestamp));
        ParityAssert.NumbersEqual(expected.Points.Select(point => point.Number).ToArray(), actual);
    }

    [Fact]
    public async Task WhenIntervalIsMadeCoarserTwice_ThenRawQueryMatchesInMemory()
    {
        // Arrange
        using var inMemory = RecordInMemory();
        using var sqlite = await RecordInSqliteAcrossIntervalChangesAsync();
        var query = new HistoryQuery(Path, Monday, To);

        // Act
        var expected = inMemory.Query(query);
        var actual = sqlite.Query(query);

        // Assert
        Assert.Equal(8, expected.Points.Length);
        Assert.Equal(expected.Points.Select(point => point.Timestamp), actual.Points.Select(point => point.Timestamp));
        Assert.Equal(expected.Points.Select(point => point.Number), actual.Points.Select(point => point.Number));
    }

    private static InMemoryParityStore RecordInMemory()
    {
        var store = new InMemoryParityStore(ParityClock.Start);
        foreach (var (_, _, samples) in Sessions)
        {
            foreach (var (hours, value) in samples)
            {
                store.Record(Path, Monday.AddHours(hours), value, typeof(double?));
            }
        }

        return store;
    }

    // Each session reopens the store under its interval where the previous one ended, so coverage is continuous.
    private async Task<SqliteHistoryStore> RecordInSqliteAcrossIntervalChangesAsync()
    {
        var now = Sessions[0].SessionStart;
        SqliteHistoryStore? store = null;
        for (var index = 0; index < Sessions.Length; index++)
        {
            store?.Dispose();
            var (interval, _, samples) = Sessions[index];
            store = new SqliteHistoryStore(
                priority: 50, databaseDirectory: _directory, interval,
                maxAge: TimeSpan.FromDays(36500), maxJsonSize: 8192, getUtcNow: () => now);
            foreach (var (hours, value) in samples)
            {
                store.Record(Path, Monday.AddHours(hours), value, typeof(double?));
            }

            now = index + 1 < Sessions.Length ? Sessions[index + 1].SessionStart : ParityClock.Now;
            await store.FlushAsync(CancellationToken.None);
        }

        return store!;
    }
}
