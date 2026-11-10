using HomeBlaze.Automation.Blazor.Editing;

namespace HomeBlaze.Automation.Blazor.Tests.Editing;

public class DurationInputTests
{
    [Theory]
    [InlineData("20s", 20)]
    [InlineData(" 10m ", 600)]
    [InlineData("1h30m", 5400)]
    [InlineData("90m", 5400)]
    public void WhenTheTextIsOneDurationLiteral_ThenItIsParsed(string text, int seconds)
    {
        // Act
        var isParsed = DurationInput.TryParse(text, out var duration);

        // Assert
        Assert.True(isParsed);
        Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
    }

    [Theory]
    [InlineData("20s + 1m")]
    [InlineData("-5m")]
    [InlineData("5")]
    [InlineData("abc")]
    [InlineData("/Demo/Motor/Temperature")]
    [InlineData("1h 30m")]
    public void WhenTheTextIsNoSingleDurationLiteral_ThenItIsRejected(string text)
    {
        // Act
        var isParsed = DurationInput.TryParse(text, out _);

        // Assert
        Assert.False(isParsed);
    }
}
