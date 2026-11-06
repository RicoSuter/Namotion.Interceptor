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
        { new TimeSpan(1, 2, 3, 4, 5), "1d2h3m4s5ms" }
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
    public void WhenTheTextIsNoDuration_ThenItIsNotParsed(string? text)
    {
        // Act
        var isParsed = AutomationDuration.TryParse(text, out _);

        // Assert
        Assert.False(isParsed);
    }

    [Fact]
    public void WhenAFormattedDurationIsParsed_ThenItIsTheSameDuration()
    {
        // Arrange
        var duration = new TimeSpan(2, 3, 4, 5, 6);

        // Act
        var isParsed = AutomationDuration.TryParse(AutomationDuration.Format(duration), out var parsed);

        // Assert
        Assert.True(isParsed);
        Assert.Equal(duration, parsed);
    }
}
