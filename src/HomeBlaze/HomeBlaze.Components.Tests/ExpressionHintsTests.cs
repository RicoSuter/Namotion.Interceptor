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
    [InlineData("/Door/OpenedAt > Limit - 5m")]
    [InlineData("/Door/OpenedAt > Limit -(5m)")]
    public void WhenTheRightSideIsMeantAsWritten_ThenThereIsNoHint(string text)
    {
        // Act
        var hints = ExpressionHints.GetBareWordHints(text, '/', path => path == "/Door/State" ? DayOfWeek.Monday : null);

        // Assert
        Assert.Empty(hints);
    }

    [Theory]
    [InlineData("/Door/OpenedAt < Now - 5m", "Now")]
    [InlineData("/a > Now/Offset", "Now")]
    [InlineData("Now - ./X > 5m", "Now")]
    [InlineData("/a > Now -(5m)", "Now")]
    [InlineData("/a > Time * 2", "Time")]
    public void WhenAPathStartsWithAVariableInAnotherCase_ThenTheHintNamesTheVariable(string text, string word)
    {
        // Act
        var hints = ExpressionHints.GetBareWordHints(text, '/', readValue: null);

        // Assert
        Assert.Equal([$"'{word}' is not the variable {word.ToLowerInvariant()}: variables are written in lowercase."], hints.ToArray());
    }

    [Fact]
    public void WhenAWordIsInParentheses_ThenTheHintIsStillGivenAndThePathIsRead()
    {
        // Arrange
        var readPaths = new List<string>();

        // Act
        var hints = ExpressionHints.GetBareWordHints("(/Weather/Temperature > Limit)", '/', path =>
        {
            readPaths.Add(path);
            return 21.5;
        });

        // Assert
        Assert.Single(hints);
        Assert.Equal(["/Weather/Temperature"], readPaths);
        Assert.Equal(["'Now' is read as the text \"Now\". The variable is written in lowercase: now."], ExpressionHints.GetBareWordHints("(/a > Now)", '/', readValue: null).ToArray());
    }
}
