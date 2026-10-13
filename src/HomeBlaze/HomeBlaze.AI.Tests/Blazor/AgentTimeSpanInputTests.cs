using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Blazor;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public class AgentTimeSpanInputTests
{
    [Theory]
    [InlineData("00:05:00", 0, 0, 5, 0)]
    [InlineData(" 1:30 ", 0, 1, 30, 0)]
    [InlineData("2.03:00:00", 2, 3, 0, 0)]
    [InlineData("00:00:00", 0, 0, 0, 0)]
    [InlineData("365.00:00:00", 365, 0, 0, 0)]
    public void WhenTextIsATimeOfDayFormat_ThenItParses(string text, int days, int hours, int minutes, int seconds)
    {
        // Act
        var isValid = AgentTimeSpanInput.TryParse(text, out var value);

        // Assert
        Assert.True(isValid);
        Assert.Equal(new TimeSpan(days, hours, minutes, seconds), value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("-00:05:00")]
    [InlineData("366.00:00:00")]
    [InlineData("abc:de")]
    public void WhenTextIsNotAnAcceptedTimeSpan_ThenParsingFails(string? text)
    {
        // Act
        var isValid = AgentTimeSpanInput.TryParse(text, out var value);

        // Assert
        Assert.False(isValid);
        Assert.Equal(TimeSpan.Zero, value);
    }

    [Fact]
    public void WhenATimeSpanIsFormatted_ThenItParsesBackToTheSameValue()
    {
        // Arrange
        var original = new TimeSpan(1, 2, 3, 4);

        // Act
        var text = AgentTimeSpanInput.Format(original);
        AgentTimeSpanInput.TryParse(text, out var parsed);

        // Assert
        Assert.Equal("1.02:03:04", text);
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("00:03:00")]
    [InlineData("24.00:00:00")]
    [InlineData("24.20:31:23")]
    public void WhenTurnTimeoutIsWithinTheEnforcedLimit_ThenItParses(string text)
    {
        // Act
        var isValid = AgentTimeSpanInput.TryParseTurnTimeout(text, out var value);

        // Assert
        Assert.True(isValid);
        Assert.True(value <= LlmAgentBase.MaximumTurnTimeout);
    }

    [Theory]
    [InlineData("24.20:31:24")]
    [InlineData("25.00:00:00")]
    [InlineData("365.00:00:00")]
    [InlineData("5")]
    public void WhenTurnTimeoutExceedsTheEnforcedLimit_ThenParsingFails(string text)
    {
        // Act
        var isValid = AgentTimeSpanInput.TryParseTurnTimeout(text, out var value);

        // Assert
        Assert.False(isValid);
        Assert.Equal(TimeSpan.Zero, value);
    }
}
