using System.Globalization;

namespace HomeBlaze.History.Abstractions.Tests;

public class BucketAlignmentInvariantTests
{
    private static readonly HistoryCalendarUnit[] Units = Enum.GetValues<HistoryCalendarUnit>();

    [Fact]
    public void WhenAnySystemZoneUnitAndInstant_ThenBucketsPartitionTime()
    {
        // Arrange - instants around every offset change from 1970 to 2040, plus a spread of plain instants.
        var from = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var violations = new List<string>();

        // Act
        foreach (var timeZone in TimeZoneInfo.GetSystemTimeZones())
        {
            var instants = new List<DateTimeOffset>();
            foreach (var transition in FindOffsetChanges(timeZone, from, to))
            {
                instants.Add(transition.AddHours(-1));
                instants.Add(transition.AddTicks(-1));
                instants.Add(transition);
                instants.Add(transition.AddMinutes(30));
                instants.Add(transition.AddHours(1));
            }

            for (var instant = from.AddHours(7.5); instant < to; instant = instant.AddDays(97.3))
            {
                instants.Add(instant);
            }

            foreach (var unit in Units)
            {
                var bucket = HistoryBucket.Calendar(unit, timeZone);
                foreach (var instant in instants)
                {
                    if (CheckInvariants(instant, bucket) is { } violation)
                    {
                        violations.Add(violation);
                    }
                }
            }
        }

        // Assert
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations.Take(50)));
    }

    [Theory]
    // A whole calendar day is skipped (the clocks jump from 00:00 to the next day).
    [InlineData("Pacific/Fakaofo", "2011-12-29T12:00:00Z")]
    [InlineData("Pacific/Fakaofo", "2011-12-30T10:59:59Z")]
    [InlineData("Pacific/Fakaofo", "2011-12-30T11:00:00Z")]
    [InlineData("Pacific/Kwajalein", "1993-08-20T12:00:00Z")]
    [InlineData("Pacific/Kwajalein", "1993-08-21T12:00:00Z")]
    // Local midnight is skipped at the start of daylight saving time.
    [InlineData("America/Asuncion", "2023-10-01T03:30:00Z")]
    [InlineData("America/Asuncion", "2023-10-01T04:30:00Z")]
    [InlineData("Asia/Amman", "2021-03-26T00:30:00Z")]
    [InlineData("Asia/Damascus", "2021-03-26T00:30:00Z")]
    [InlineData("America/Santiago", "2037-09-06T03:30:00Z")]
    // The clocks go back across midnight, from 00:01 to 23:01 of the day before.
    [InlineData("America/St_Johns", "2006-10-29T02:45:00Z")]
    [InlineData("America/St_Johns", "2006-10-29T03:15:00Z")]
    public void WhenZoneHasAnIrregularMidnight_ThenBucketsPartitionTime(string zoneId, string instant)
    {
        // Arrange
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var timestamp = Parse(instant);
        var violations = new List<string>();

        // Act
        foreach (var unit in Units)
        {
            var bucket = HistoryBucket.Calendar(unit, timeZone);
            foreach (var offset in new[] { -36, -12, -1, 0, 1, 12, 36 })
            {
                if (CheckInvariants(timestamp.AddHours(offset), bucket) is { } violation)
                {
                    violations.Add(violation);
                }
            }
        }

        // Assert
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void WhenDayIsSkipped_ThenItsBucketIsEmptyAndTheNextStartsAtTheJump()
    {
        // Arrange - Fakaofo went from -11:00 to +13:00 at local midnight of 2011-12-30, so that day never happened.
        var bucket = HistoryBucket.Calendar(HistoryCalendarUnit.Day, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Fakaofo"));

        // Act
        var start = BucketAlignment.Start(Parse("2011-12-29T12:00:00Z"), bucket);
        var next = BucketAlignment.Next(start, bucket);
        var afterNext = BucketAlignment.Next(next, bucket);

        // Assert - 2011-12-29 local runs to the jump, and 2011-12-31 local starts there.
        Assert.Equal(Parse("2011-12-29T11:00:00Z"), start);
        Assert.Equal(Parse("2011-12-30T11:00:00Z"), next);
        Assert.Equal(Parse("2011-12-31T11:00:00Z"), afterNext);
    }

    [Fact]
    public void WhenMidnightIsSkippedAtDaylightSavingStart_ThenDayStartsAtTheTransition()
    {
        // Arrange - Asuncion went from -04:00 to -03:00 at local midnight of 2023-10-01.
        var bucket = HistoryBucket.Calendar(HistoryCalendarUnit.Day, TimeZoneInfo.FindSystemTimeZoneById("America/Asuncion"));

        // Act
        var start = BucketAlignment.Start(Parse("2023-10-01T03:30:00Z"), bucket);
        var next = BucketAlignment.Next(start, bucket);

        // Assert
        Assert.Equal(Parse("2023-09-30T04:00:00Z"), start);
        Assert.Equal(Parse("2023-10-01T04:00:00Z"), next);
    }

    private static string? CheckInvariants(DateTimeOffset instant, HistoryBucket bucket)
    {
        var start = BucketAlignment.Start(instant, bucket);
        var next = BucketAlignment.Next(start, bucket);
        var startOfNext = BucketAlignment.Start(next, bucket);

        // A start is the first instant whose local time reaches the first day of its period.
        var localStart = TimeZoneInfo.ConvertTime(start, bucket.TimeZone!).DateTime;
        var localBeforeStart = TimeZoneInfo.ConvertTime(start.AddTicks(-1), bucket.TimeZone!).DateTime;
        var isPeriodStart = localBeforeStart < PeriodFirstDay(localStart, bucket.CalendarUnit!.Value);

        if (start <= instant && instant < next && startOfNext == next && isPeriodStart)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{bucket}: instant {instant:O}, start {start:O}, next {next:O}, start of next {startOfNext:O}");
    }

    private static DateTime PeriodFirstDay(DateTime localTime, HistoryCalendarUnit unit)
    {
        var date = localTime.Date;
        return unit switch
        {
            HistoryCalendarUnit.Day => date,
            HistoryCalendarUnit.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
            HistoryCalendarUnit.Month => new DateTime(date.Year, date.Month, 1),
            _ => new DateTime(date.Year, 1, 1)
        };
    }

    // Finds the instants where the zone's UTC offset changes, by weekly steps and bisection.
    private static IEnumerable<DateTimeOffset> FindOffsetChanges(TimeZoneInfo timeZone, DateTimeOffset from, DateTimeOffset to)
    {
        var previous = from;
        var previousOffset = timeZone.GetUtcOffset(previous);
        for (var current = from.AddDays(7); current < to; current = current.AddDays(7))
        {
            var currentOffset = timeZone.GetUtcOffset(current);
            if (currentOffset != previousOffset)
            {
                var low = previous;
                var high = current;
                while ((high - low).Ticks > 1)
                {
                    var middle = low + (high - low) / 2;
                    if (timeZone.GetUtcOffset(middle) == previousOffset)
                    {
                        low = middle;
                    }
                    else
                    {
                        high = middle;
                    }
                }

                yield return high;
            }

            previous = current;
            previousOffset = currentOffset;
        }
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
