using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

public sealed class SqlitePartitionTests
{
    [Fact]
    public void WhenWeeklyKey_ThenAnchorsOnMonday()
    {
        // Arrange - Wednesday 2026-06-24 (the ISO week starts Monday 2026-06-22)
        var wednesday = new DateTimeOffset(2026, 6, 24, 9, 30, 0, TimeSpan.Zero);

        // Act
        var key = SqlitePartition.PartitionKey(wednesday, PartitionInterval.Weekly);

        // Assert
        Assert.Equal("2026-06-22", key);
    }

    [Fact]
    public void WhenWeeklyKeyOnSunday_ThenAnchorsOnPrecedingMonday()
    {
        // Arrange - Sunday 2026-06-28 still belongs to the week starting Monday 2026-06-22
        var sunday = new DateTimeOffset(2026, 6, 28, 23, 59, 0, TimeSpan.Zero);

        // Act
        var key = SqlitePartition.PartitionKey(sunday, PartitionInterval.Weekly);

        // Assert
        Assert.Equal("2026-06-22", key);
    }

    [Fact]
    public void WhenWeeklyPartitionRange_ThenRoundTripsToMondayWeek()
    {
        // Arrange
        const string key = "2026-06-22";

        // Act
        var (start, end) = SqlitePartition.PartitionRange(key, PartitionInterval.Weekly);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 6, 29, 0, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void WhenDailyPartitionRange_ThenRoundTripsToSingleDay()
    {
        // Arrange
        var timestamp = new DateTimeOffset(2026, 6, 24, 14, 0, 0, TimeSpan.Zero);

        // Act
        var key = SqlitePartition.PartitionKey(timestamp, PartitionInterval.Daily);
        var (start, end) = SqlitePartition.PartitionRange(key, PartitionInterval.Daily);

        // Assert
        Assert.Equal("2026-06-24", key);
        Assert.Equal(new DateTimeOffset(2026, 6, 24, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void WhenMonthlyPartitionRange_ThenRoundTripsToSingleMonth()
    {
        // Arrange
        var timestamp = new DateTimeOffset(2026, 6, 24, 14, 0, 0, TimeSpan.Zero);

        // Act
        var key = SqlitePartition.PartitionKey(timestamp, PartitionInterval.Monthly);
        var (start, end) = SqlitePartition.PartitionRange(key, PartitionInterval.Monthly);

        // Assert
        Assert.Equal("2026-06", key);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void WhenRangeSpansTwoWeeks_ThenEnumerateReturnsBothKeys()
    {
        // Arrange - from a Tuesday in one week to a Tuesday in the next week
        var from = new DateTimeOffset(2026, 6, 23, 8, 0, 0, TimeSpan.Zero); // week of 2026-06-22
        var to = new DateTimeOffset(2026, 6, 30, 8, 0, 0, TimeSpan.Zero);   // week of 2026-06-29

        // Act
        var keys = SqlitePartition.EnumeratePartitionKeys(from, to, PartitionInterval.Weekly).ToArray();

        // Assert
        Assert.Equal(new[] { "2026-06-22", "2026-06-29" }, keys);
    }

    [Fact]
    public void WhenRangeWithinSingleWeek_ThenEnumerateReturnsOneKey()
    {
        // Arrange
        var from = new DateTimeOffset(2026, 6, 23, 8, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 6, 25, 8, 0, 0, TimeSpan.Zero);

        // Act
        var keys = SqlitePartition.EnumeratePartitionKeys(from, to, PartitionInterval.Weekly).ToArray();

        // Assert
        Assert.Equal(new[] { "2026-06-22" }, keys);
    }
}
