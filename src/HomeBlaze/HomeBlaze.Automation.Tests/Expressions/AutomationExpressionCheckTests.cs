using HomeBlaze.Automation.Expressions;

namespace HomeBlaze.Automation.Tests.Expressions;

public class AutomationExpressionCheckTests
{
    private const string EventOnly = "is only available in event filters and in the arguments of a rule with an event trigger";

    [Theory]
    [InlineData("now - ./LastTriggeredAt > 30m", false, null)]
    [InlineData("time >= 8h30m and time < 21h30m", false, null)]
    [InlineData("event/Gesture == Release", true, null)]
    [InlineData("subject/Name == WS90", true, null)]
    [InlineData("value > 1", true, "'value' is not available in automations")]
    [InlineData("event/Gesture == Release", false, "'event' " + EventOnly)]
    [InlineData("subject/Name == WS90", false, "'subject' " + EventOnly)]
    [InlineData("now/Ticks > 1", false, "'now' has no members")]
    [InlineData("/Weather/Gust | raw", false, "An expression of an automation takes no pipes")]
    [InlineData("", false, "the condition is empty")]
    [InlineData("   ", false, "the condition is empty")]
    [InlineData("/Weather/Gust >", false, "expected a value after '>' at position 15")]
    public void WhenAnExpressionIsParsed_ThenItsVariablesAndPipesMustFitAutomations(string text, bool allowsEvent, string? expectedError)
    {
        // Act
        var isParsed = AutomationExpressionCheck.TryParseExpression(text, allowsEvent, out var expression, out var error);

        // Assert
        Assert.Equal(expectedError is null, isParsed);
        Assert.Equal(expectedError is null, expression is not null);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData("Gust {{ /Weather/Gust | raw }} m/s", false, null)]
    [InlineData("Plain text", false, null)]
    [InlineData("{{ event/Gesture }}", true, null)]
    [InlineData("{{ /Weather/Gust | rounded }}", false, "Unknown pipe 'rounded'")]
    [InlineData("{{ /Weather/Gust | raw:2 }}", false, "The pipe 'raw' takes no arguments")]
    [InlineData("{{ event/Gesture }}", false, "'event' " + EventOnly)]
    [InlineData("{{ /Weather/Gust", false, "'{{' is not closed at position 0")]
    public void WhenATemplateIsParsed_ThenItsExpressionsMustFitAutomations(string text, bool allowsEvent, string? expectedError)
    {
        // Act
        var isParsed = AutomationExpressionCheck.TryParseTemplate(text, allowsEvent, out var template, out var error);

        // Assert
        Assert.Equal(expectedError is null, isParsed);
        Assert.Equal(expectedError is null, template is not null);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData("now - /Weather/LastUpdated > 15m", true)]
    [InlineData("time >= 8h30m", true)]
    [InlineData("/Weather/Gust > 8", false)]
    public void WhenAnExpressionReadsTheClock_ThenItUsesTime(string text, bool expected)
    {
        // Arrange
        Assert.True(AutomationExpressionCheck.TryParseExpression(text, allowsEvent: false, out var expression, out var error), error);

        // Act
        var usesTime = AutomationExpressionCheck.UsesTime(expression);

        // Assert
        Assert.Equal(expected, usesTime);
    }

    [Fact]
    public void WhenPropertyPathsAreListed_ThenVariablesAreLeftOut()
    {
        // Arrange
        Assert.True(AutomationExpressionCheck.TryParseExpression(
            "now - ./LastTriggeredAt > 30m and /Weather/Gust > 8", allowsEvent: false, out var expression, out var error), error);

        // Act
        var paths = AutomationExpressionCheck.GetPropertyPaths(expression).ToArray();

        // Assert
        Assert.Equal(["./LastTriggeredAt", "/Weather/Gust"], paths);
    }
}
