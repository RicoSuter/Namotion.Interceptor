using HomeBlaze.Components.Inputs;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ExpressionHintsTests
{
    [Fact]
    public void WhenABareWordDiffersFromAVariableOnlyInCase_ThenTheHintNamesTheVariable()
    {
        // Act
        var hints = ExpressionHints.GetBareWordHints("/Door/OpenedAt > Now", '/', readValue: null);

        // Assert
        Assert.Equal(["'Now' is read as the text \"Now\". The variable is written in lowercase: now."], hints.ToArray());
    }

    [Fact]
    public void WhenABareWordIsComparedWithANumber_ThenTheHintSaysItNeverMatches()
    {
        // Act
        var hints = ExpressionHints.GetBareWordHints("/Weather/Temperature > Limit", '/', path => path == "/Weather/Temperature" ? 21.5 : null);

        // Assert
        Assert.Equal(
            ["'Limit' is read as the text \"Limit\", which never equals the value of /Weather/Temperature. Write a path with '/', or a number."],
            hints.ToArray());
    }

    [Theory]
    [InlineData("/Door/State == Open")]
    [InlineData("/Door/OpenedAt > now")]
    [InlineData("/Door/OpenedAt > 'Now'")]
    [InlineData("/Weather/Temperature > /Weather/Limit")]
    [InlineData("/Weather/Temperature > 20")]
    [InlineData("/Weather/Temperature == null")]
    [InlineData("/Door/OpenedAt > Now - 5m")]
    [InlineData("/Door/OpenedAt > Now/Offset")]
    public void WhenTheRightSideIsMeantAsWritten_ThenThereIsNoHint(string text)
    {
        // Act
        var hints = ExpressionHints.GetBareWordHints(text, '/', path => path == "/Door/State" ? DayOfWeek.Monday : null);

        // Assert
        Assert.Empty(hints);
    }
}
