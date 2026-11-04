using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Mcp.Tests;

public class HistoryToolParsingTests
{
    [Fact]
    public void WhenAggregationOmitted_ThenDefaultsToLast()
    {
        // Arrange & Act
        var result = HistoryToolParsing.NormalizeAggregation(null);

        // Assert
        Assert.Equal(HistoryAggregations.Last, result);
    }

    [Theory]
    [InlineData("last", "Last")]
    [InlineData("TIMEWEIGHTEDAVERAGE", "TimeWeightedAverage")]
    [InlineData("StandardDeviation", "StandardDeviation")]
    public void WhenAggregationVariesByCase_ThenNormalizedToCanonical(string input, string expected)
    {
        // Act
        var result = HistoryToolParsing.NormalizeAggregation(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenAggregationUnknown_ThenReturnsNull()
    {
        // Act
        var result = HistoryToolParsing.NormalizeAggregation("bogus");

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("5m", 0, 5, 0)]
    [InlineData("30s", 0, 0, 30)]
    [InlineData("1h", 1, 0, 0)]
    [InlineData("00:05:00", 0, 5, 0)]
    public void WhenBucketHasUnitOrTimeSpan_ThenParsed(string input, int hours, int minutes, int seconds)
    {
        // Act
        var result = HistoryToolParsing.ParseBucket(input);

        // Assert
        Assert.Equal(new TimeSpan(hours, minutes, seconds), result);
    }

    [Fact]
    public void WhenBucketOmitted_ThenNull()
    {
        // Act & Assert
        Assert.Null(HistoryToolParsing.ParseBucket(null));
        Assert.Null(HistoryToolParsing.ParseBucket("  "));
    }

    [Theory]
    [InlineData("5x")]   // unknown unit
    [InlineData("5")]    // bare number (would silently be 5 days)
    [InlineData("0s")]   // zero
    [InlineData("-5m")]  // negative
    public void WhenBucketInvalid_ThenThrowsFormat(string input)
    {
        // Act & Assert
        Assert.Throws<FormatException>(() => HistoryToolParsing.ParseBucket(input));
    }

    [Theory]
    [InlineData(typeof(double), "number")]
    [InlineData(typeof(int), "number")]
    [InlineData(typeof(decimal), "number")]
    [InlineData(typeof(long?), "number")]
    [InlineData(typeof(bool), "boolean")]
    [InlineData(typeof(string), "string")]
    [InlineData(typeof(DayOfWeek), "enum")]
    public void WhenDerivingValueType_ThenMapsByType(Type type, string expected)
    {
        // Act
        var result = HistoryToolParsing.ValueType(type);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenBareTimestamp_ThenParsedAsLocalTimeInTheZone()
    {
        // Act
        var result = HistoryToolParsing.ParseTimestamp("2026-06-24T10:00:00", Zurich);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 6, 24, 8, 0, 0, TimeSpan.Zero), result);
    }

    [Theory]
    [InlineData("2026-06-24T10:00:00Z", "2026-06-24T10:00:00Z")]
    [InlineData("2026-06-24T10:00:00+05:00", "2026-06-24T05:00:00Z")]
    // A bare date is local midnight.
    [InlineData("2026-01-01", "2025-12-31T23:00:00Z")]
    // A local time in the spring gap moves to the instant after it; a repeated one takes the earlier instant.
    [InlineData("2026-03-29T02:30:00", "2026-03-29T01:00:00Z")]
    [InlineData("2026-10-25T02:30:00", "2026-10-25T00:30:00Z")]
    public void WhenTimestampIsParsedInAZone_ThenAnExplicitOffsetIsHonoredAndABareOneIsLocal(string raw, string expected)
    {
        // Act
        var result = HistoryToolParsing.ParseTimestamp(raw, Zurich);

        // Assert
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result);
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00")]
    [InlineData("not a time")]
    public void WhenTimestampCannotBeResolved_ThenFormatException(string raw)
    {
        // Act & Assert - the tool turns a FormatException into a structured error.
        Assert.Throws<FormatException>(() => HistoryToolParsing.ParseTimestamp(raw, Zurich));
    }

    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    [Theory]
    [InlineData("day", HistoryCalendarUnit.Day)]
    [InlineData("Week", HistoryCalendarUnit.Week)]
    [InlineData("MONTH", HistoryCalendarUnit.Month)]
    [InlineData(" year ", HistoryCalendarUnit.Year)]
    public void WhenBucketIsACalendarWord_ThenParsesTheUnit(string raw, HistoryCalendarUnit expected)
    {
        // Act
        var unit = HistoryToolParsing.ParseCalendarUnit(raw);

        // Assert
        Assert.Equal(expected, unit);
    }

    [Theory]
    [InlineData("5m")]
    [InlineData(null)]
    [InlineData("days")]
    public void WhenBucketIsNotACalendarWord_ThenReturnsNull(string? raw)
    {
        // Act
        var unit = HistoryToolParsing.ParseCalendarUnit(raw);

        // Assert
        Assert.Null(unit);
    }

    [Fact]
    public void WhenTimeZoneIsUnknown_ThenThrowsFormatException()
    {
        // Act & Assert
        var exception = Assert.Throws<FormatException>(() => HistoryToolParsing.ParseTimeZone("Mars/Olympus"));
        Assert.Equal("Unknown time zone 'Mars/Olympus'.", exception.Message);
    }

    [Theory]
    [InlineData("/Europe/Zurich")]
    [InlineData("Europe/../Zurich")]
    [InlineData("Europe/Zurich\\")]
    [InlineData("")]
    public void WhenTimeZoneIdIsNotAPlainIanaId_ThenThrowsFormatException(string id)
    {
        // Act & Assert
        var exception = Assert.Throws<FormatException>(() => HistoryToolParsing.ParseTimeZone(id));
        Assert.Equal($"Unknown time zone '{id}'.", exception.Message);
    }

    [Theory]
    [InlineData("Europe/Zurich")]
    [InlineData("Etc/GMT+1")]
    [InlineData("America/Port-au-Prince")]
    [InlineData("UTC")]
    public void WhenTimeZoneIdIsAnIanaId_ThenItResolves(string id)
    {
        // Act
        var timeZone = HistoryToolParsing.ParseTimeZone(id);

        // Assert
        Assert.NotNull(timeZone);
    }
}
