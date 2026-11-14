namespace HomeBlaze.Automation.Tests;

public class AutomationDurationTests
{
    public static TheoryData<TimeSpan, string> FormattedDurations => new()
    {
        { TimeSpan.FromSeconds(20), "20s" },
        { TimeSpan.FromMinutes(90), "1h30m" },
        { TimeSpan.FromHours(36), "1d12h" },
        { TimeSpan.FromMilliseconds(500), "500ms" },
        { TimeSpan.Zero, "0s" },
        { TimeSpan.FromMinutes(-5), "-5m" },
        { new TimeSpan(1, 2, 3, 4, 5), "1d2h3m4s5ms" },
        { TimeSpan.FromTicks(5000), "0.5ms" },
        { TimeSpan.FromTicks(15000), "1.5ms" },
        { TimeSpan.FromDays(1) + TimeSpan.FromTicks(1), "1d0.0001ms" },
        { -TimeSpan.FromTicks(12345), "-1.2345ms" },
        { TimeSpan.MinValue, "-10675199d2h48m5s477.5808ms" }
    };

    [Theory]
    [MemberData(nameof(FormattedDurations))]
    public void WhenADurationIsFormatted_ThenItIsWrittenWithItsUnits(TimeSpan duration, string expected)
    {
        // Act
        var text = AutomationDuration.Format(duration);

        // Assert
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("20s", 0, 0, 20)]
    [InlineData(" 1h30m ", 1, 30, 0)]
    [InlineData("8h30m", 8, 30, 0)]
    [InlineData("1.5d", 36, 0, 0)]
    [InlineData("-5m", 0, -5, 0)]
    public void WhenADurationLiteralIsParsed_ThenItIsTheDuration(string text, int hours, int minutes, int seconds)
    {
        // Act
        var isParsed = AutomationDuration.TryParse(text, out var duration);

        // Assert
        Assert.True(isParsed);
        Assert.Equal(new TimeSpan(hours, minutes, seconds), duration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("20")]
    [InlineData("abc")]
    [InlineData("now")]
    [InlineData("20s > 1s")]
    [InlineData("20 s")]
    [InlineData("36501d")]
    [InlineData("-36500d - 1ms")]
    public void WhenTheTextIsNoDuration_ThenItIsNotParsed(string? text)
    {
        // Act
        var isParsed = AutomationDuration.TryParse(text, out _);

        // Assert
        Assert.False(isParsed);
    }

    [Theory]
    [InlineData("36501d")]
    [InlineData("-36501d")]
    public void WhenTheDurationIsLongerThanTheMaximum_ThenItIsNotParsedAndIsZero(string text)
    {
        // Act
        var isParsed = AutomationDuration.TryParse(text, out var duration);

        // Assert
        Assert.False(isParsed);
        Assert.Equal(TimeSpan.Zero, duration);
    }

    [Fact]
    public void WhenTheDurationIsTheMaximum_ThenItIsParsed()
    {
        // Act
        var isParsed = AutomationDuration.TryParse("36500d", out var duration);

        // Assert
        Assert.True(isParsed);
        Assert.Equal(AutomationDuration.Maximum, duration);
    }

    public static TheoryData<TimeSpan> RoundTripDurations => new()
    {
        new TimeSpan(2, 3, 4, 5, 6),
        TimeSpan.FromTicks(5000),
        -TimeSpan.FromTicks(12345),
        AutomationDuration.Maximum - TimeSpan.FromTicks(1)
    };

    [Theory]
    [MemberData(nameof(RoundTripDurations))]
    public void WhenAFormattedDurationIsParsed_ThenItIsTheSameDuration(TimeSpan duration)
    {
        // Act
        var isParsed = AutomationDuration.TryParse(AutomationDuration.Format(duration), out var parsed);

        // Assert
        Assert.True(isParsed);
        Assert.Equal(duration, parsed);
    }
}
