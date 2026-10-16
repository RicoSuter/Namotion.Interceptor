using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Abstractions.Tests;

public class BucketAlignmentTests
{
    [Fact]
    public void WhenTimestampIsInsideBucket_ThenFloorsToBucketBoundary()
    {
        // Arrange
        var bucket = TimeSpan.FromMinutes(10);
        var timestamp = new DateTimeOffset(2026, 6, 7, 12, 34, 56, TimeSpan.Zero);

        // Act
        var start = BucketAlignment.BucketStart(timestamp, bucket);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 6, 7, 12, 30, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void WhenTimestampIsOnBoundary_ThenReturnsSameTimestamp()
    {
        // Arrange
        var bucket = TimeSpan.FromHours(1);
        var boundary = new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero);

        // Act
        var start = BucketAlignment.BucketStart(boundary, bucket);

        // Assert
        Assert.Equal(boundary, start);
    }

    [Fact]
    public void WhenAnchoredAtEpoch_ThenBucketsAlignToEpoch()
    {
        // Arrange
        var bucket = TimeSpan.FromMinutes(15);
        var timestamp = DateTimeOffset.UnixEpoch.AddMinutes(37);

        // Act
        var start = BucketAlignment.BucketStart(timestamp, bucket);

        // Assert
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(30), start);
    }

    [Fact]
    public void WhenAppliedTwice_ThenIsIdempotent()
    {
        // Arrange
        var bucket = TimeSpan.FromSeconds(30);
        var timestamp = new DateTimeOffset(2026, 6, 7, 12, 34, 56, 789, TimeSpan.Zero);

        // Act
        var once = BucketAlignment.BucketStart(timestamp, bucket);
        var twice = BucketAlignment.BucketStart(once, bucket);

        // Assert
        Assert.Equal(once, twice);
    }

    [Fact]
    public void WhenTimestampPredatesUnixEpoch_ThenFloorsTowardEarlierBoundary()
    {
        // Arrange
        var bucket = TimeSpan.FromSeconds(10);
        var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(-1);

        // Act
        var start = BucketAlignment.BucketStart(timestamp, bucket);

        // Assert
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(-10), start);
    }

    [Fact]
    public void WhenBucketIsNotPositive_ThenThrows()
    {
        // Arrange
        var timestamp = DateTimeOffset.UnixEpoch;

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BucketAlignment.BucketStart(timestamp, TimeSpan.Zero));
    }

    [Fact]
    public void WhenRangeContainsMoreThanMaximumBuckets_ThenFirstBucketSkipsOldest()
    {
        // Arrange
        var bucket = TimeSpan.FromSeconds(10);
        var from = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var to = DateTimeOffset.UnixEpoch.AddSeconds(51);

        // Act
        var start = BucketAlignment.FirstBucketStart(from, to, bucket, maxBuckets: 2);

        // Assert
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(40), start);
    }

    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    [Theory]
    // DST starts 2026-03-29 02:00: the day is 23 hours.
    [InlineData("2026-03-29T12:00:00Z", HistoryCalendarUnit.Day, "2026-03-28T23:00:00Z", "2026-03-29T22:00:00Z")]
    // DST ends 2026-10-25 03:00: the day is 25 hours.
    [InlineData("2026-10-25T12:00:00Z", HistoryCalendarUnit.Day, "2026-10-24T22:00:00Z", "2026-10-25T23:00:00Z")]
    // A local midnight just after the UTC one belongs to the next local day.
    [InlineData("2026-06-01T22:30:00Z", HistoryCalendarUnit.Day, "2026-06-01T22:00:00Z", "2026-06-02T22:00:00Z")]
    // Thursday 2026-10-15: the ISO week starts Monday 2026-10-12.
    [InlineData("2026-10-15T12:00:00Z", HistoryCalendarUnit.Week, "2026-10-11T22:00:00Z", "2026-10-18T22:00:00Z")]
    [InlineData("2026-11-15T00:00:00Z", HistoryCalendarUnit.Month, "2026-10-31T23:00:00Z", "2026-11-30T23:00:00Z")]
    [InlineData("2026-03-31T21:59:59Z", HistoryCalendarUnit.Month, "2026-02-28T23:00:00Z", "2026-03-31T22:00:00Z")]
    [InlineData("2026-06-01T00:00:00Z", HistoryCalendarUnit.Year, "2025-12-31T23:00:00Z", "2026-12-31T23:00:00Z")]
    public void WhenCalendarBucketInZurich_ThenStartAndNextAreLocalPeriodBoundaries(
        string timestamp, HistoryCalendarUnit unit, string expectedStart, string expectedNext)
    {
        // Arrange
        var bucket = HistoryBucket.Calendar(unit, Zurich);

        // Act
        var start = BucketAlignment.Start(Parse(timestamp), bucket);
        var next = BucketAlignment.Next(start, bucket);

        // Assert
        Assert.Equal(Parse(expectedStart), start);
        Assert.Equal(Parse(expectedNext), next);
        Assert.Equal(TimeSpan.Zero, start.Offset);
    }

    [Fact]
    public void WhenZoneHasHalfHourOffset_ThenDayStartsAtLocalMidnight()
    {
        // Arrange
        var bucket = HistoryBucket.Calendar(HistoryCalendarUnit.Day, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        // Act
        var start = BucketAlignment.Start(Parse("2026-06-01T00:00:00Z"), bucket);

        // Assert
        Assert.Equal(Parse("2026-05-31T18:30:00Z"), start);
    }

    [Fact]
    public void WhenLocalMidnightIsSkipped_ThenDayStartsAtTheTransition()
    {
        // Arrange - a zone at +01:00 whose clocks jump from 00:00 to 01:00 on 2026-03-29.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 29),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 25));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/MidnightJump", TimeSpan.FromHours(1), "Midnight jump", "Standard", "Daylight", [rule]);
        var bucket = HistoryBucket.Calendar(HistoryCalendarUnit.Day, zone);

        // Act
        var start = BucketAlignment.Start(Parse("2026-03-29T12:00:00Z"), bucket);
        var next = BucketAlignment.Next(start, bucket);

        // Assert
        Assert.Equal(Parse("2026-03-28T23:00:00Z"), start);
        Assert.Equal(Parse("2026-03-29T22:00:00Z"), next);
    }

    [Fact]
    public void WhenLocalMidnightOccursTwice_ThenDayStartsAtTheEarlierInstant()
    {
        // Arrange - a zone at +01:00 (+02:00 in summer) whose clocks go back from 01:00 to 00:00 on 2026-10-25,
        // so local midnight occurs at 22:00 UTC (+02:00) and again at 23:00 UTC (+01:00) on 2026-10-24.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 29),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 25));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/MidnightTwice", TimeSpan.FromHours(1), "Midnight twice", "Standard", "Daylight", [rule]);
        var bucket = HistoryBucket.Calendar(HistoryCalendarUnit.Day, zone);

        // Act
        var start = BucketAlignment.Start(Parse("2026-10-25T12:00:00Z"), bucket);
        var startFromSecondMidnight = BucketAlignment.Start(Parse("2026-10-24T23:30:00Z"), bucket);
        var next = BucketAlignment.Next(start, bucket);
        var previousNext = BucketAlignment.Next(BucketAlignment.Start(start.AddTicks(-1), bucket), bucket);

        // Assert - the day runs 25 hours, from the first midnight to the next one at +01:00.
        Assert.Equal(Parse("2026-10-24T22:00:00Z"), start);
        Assert.Equal(start, startFromSecondMidnight);
        Assert.Equal(start, BucketAlignment.Start(start, bucket));
        Assert.Equal(Parse("2026-10-25T23:00:00Z"), next);
        Assert.Equal(start, previousNext);
    }

    [Fact]
    public void WhenMaxBucketsClipsCalendarRange_ThenFirstBucketStartKeepsTheNewest()
    {
        // Arrange - January to June is six months; keep the newest three.
        var bucket = HistoryBucket.Calendar(HistoryCalendarUnit.Month, Zurich);

        // Act
        var clipped = BucketAlignment.FirstBucketStart(Parse("2026-01-15T00:00:00Z"), Parse("2026-06-15T00:00:00Z"), bucket, 3);
        var unclipped = BucketAlignment.FirstBucketStart(Parse("2026-01-15T00:00:00Z"), Parse("2026-06-15T00:00:00Z"), bucket, 100);

        // Assert
        Assert.Equal(Parse("2026-03-31T22:00:00Z"), clipped);
        Assert.Equal(Parse("2025-12-31T23:00:00Z"), unclipped);
    }

    [Fact]
    public void WhenFixedBucketConvertsFromTimeSpan_ThenAlignmentMatchesTheTimeSpanOverload()
    {
        // Arrange
        HistoryBucket bucket = TimeSpan.FromMinutes(5);
        var timestamp = Parse("2026-06-01T10:07:30Z");

        // Act
        var start = BucketAlignment.Start(timestamp, bucket);

        // Assert
        Assert.Equal(BucketAlignment.BucketStart(timestamp, TimeSpan.FromMinutes(5)), start);
        Assert.Equal(start.AddMinutes(5), BucketAlignment.Next(start, bucket));
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
