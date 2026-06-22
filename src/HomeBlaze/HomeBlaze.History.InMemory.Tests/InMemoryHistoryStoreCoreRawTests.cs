using System.Text.Json;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.InMemory;

namespace HomeBlaze.History.InMemory.Tests;

public class InMemoryHistoryStoreCoreRawTests
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private static InMemoryHistoryStoreCore NewCore(DateTimeOffset now, int maxPoints = 1000, int maxAgeSeconds = 60) =>
        new(maxPointsPerProperty: maxPoints, maxAge: TimeSpan.FromSeconds(maxAgeSeconds),
            maxJsonSize: 8192, getUtcNow: () => now);

    [Fact]
    public void WhenDoublesRecorded_ThenRawQueryReturnsNumbersAscending()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Value", Base.AddSeconds(0), 1.5d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(1), 2.5d, typeof(double));

        // Act
        var series = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(10)));

        // Assert
        Assert.False(series.Truncated);
        Assert.Equal(new double?[] { 1.5, 2.5 }, series.Points.Select(point => point.Number).ToArray());
        Assert.All(series.Points, point => Assert.Null(point.Json));
    }

    [Fact]
    public void WhenLongAndBoolRecorded_ThenStoredInNumberColumn()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Count", Base.AddSeconds(0), 42L, typeof(long));
        core.Record("/a/Flag", Base.AddSeconds(0), true, typeof(bool));

        // Act
        var counts = core.Query(new HistoryQuery("/a/Count", Base, Base.AddSeconds(10)));
        var flags = core.Query(new HistoryQuery("/a/Flag", Base, Base.AddSeconds(10)));

        // Assert
        Assert.Equal(42d, counts.Points.Single().Number);
        Assert.Equal(1d, flags.Points.Single().Number); // bool as 1/0
    }

    [Fact]
    public void WhenStringRecorded_ThenStoredInJsonColumn()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Name", Base.AddSeconds(0), "hello", typeof(string));

        // Act
        var series = core.Query(new HistoryQuery("/a/Name", Base, Base.AddSeconds(10)));

        // Assert
        var point = series.Points.Single();
        Assert.Null(point.Number);
        Assert.Equal(JsonValueKind.String, point.Json!.Value.ValueKind);
        Assert.Equal("hello", point.Json!.Value.GetString());
    }

    [Fact]
    public void WhenEnumRecorded_ThenStoredAsName()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Mode", Base.AddSeconds(0), DayOfWeek.Friday, typeof(DayOfWeek));

        // Act
        var point = core.Query(new HistoryQuery("/a/Mode", Base, Base.AddSeconds(10))).Points.Single();

        // Assert
        Assert.Equal("Friday", point.Json!.Value.GetString());
    }

    [Fact]
    public void WhenNullRecorded_ThenPointHasNoValueColumns()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Value", Base.AddSeconds(0), null, typeof(double?));

        // Act
        var point = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(10))).Points.Single();

        // Assert - explicit recorded null: a real point, both columns null
        Assert.Equal(Base, point.Timestamp);
        Assert.Null(point.Number);
        Assert.Null(point.Json);
    }

    [Fact]
    public void WhenMorePointsThanBudget_ThenNewestKeptAndTruncatedSet()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(100));
        for (var i = 0; i < 5; i++)
        {
            core.Record("/a/Value", Base.AddSeconds(i), (double)i, typeof(double));
        }

        // Act - budget of 2 keeps the two newest, ascending
        var series = core.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(100), MaxPoints: 2));

        // Assert
        Assert.True(series.Truncated);
        Assert.Equal(new double?[] { 3, 4 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public void WhenUnknownPath_ThenEmptyNotTruncated()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(10));

        // Act
        var series = core.Query(new HistoryQuery("/missing", Base, Base.AddSeconds(10)));

        // Assert
        Assert.Empty(series.Points);
        Assert.False(series.Truncated);
        Assert.Equal("/missing", series.PropertyPath);
    }

    [Fact]
    public void WhenGetSampleAtOrBefore_ThenReturnsHeldValueOrNull()
    {
        // Arrange
        var core = NewCore(Base.AddSeconds(10));
        core.Record("/a/Value", Base.AddSeconds(0), 7d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(5), 9d, typeof(double));

        // Act
        var held = core.GetSampleAtOrBefore("/a/Value", Base.AddSeconds(3));
        var beforeAll = core.GetSampleAtOrBefore("/a/Value", Base.AddSeconds(-1));

        // Assert
        Assert.Equal(7d, held!.Number);
        Assert.Null(beforeAll);
    }

    [Fact]
    public void WhenCoverageRequested_ThenSpansMaxAgeWindowToNow()
    {
        // Arrange
        var now = Base.AddSeconds(120);
        var core = NewCore(now, maxAgeSeconds: 60);
        core.Record("/a/Value", Base.AddSeconds(70), 1d, typeof(double));

        // Act
        var coverage = core.Coverage;

        // Assert - From = max(startTime, now-60s); startTime == now here (set in ctor), so From == now-?
        Assert.Equal(now, coverage.To);
        Assert.True(coverage.From <= now);
        Assert.True(coverage.From >= now - TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void WhenSwept_ThenSamplesOlderThanMaxAgeEvicted()
    {
        // Arrange - now is Base+120s, MaxAge 60s, so anything before Base+60s is stale
        var now = Base.AddSeconds(120);
        var core = NewCore(now, maxAgeSeconds: 60);
        core.Record("/a/Value", Base.AddSeconds(10), 1d, typeof(double)); // stale
        core.Record("/a/Value", Base.AddSeconds(90), 2d, typeof(double)); // fresh

        // Act
        core.Sweep();
        var series = core.Query(new HistoryQuery("/a/Value", Base, now.AddSeconds(1)));

        // Assert
        Assert.Equal(new double?[] { 2 }, series.Points.Select(point => point.Number).ToArray());
        Assert.Equal(1, core.EvictedCount);
    }
}
