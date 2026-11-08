using System.Collections.Immutable;
using HomeBlaze.Components.Inputs;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ExpressionHintsTests
{
    private static readonly ImmutableArray<string> Variables = ["now", "time", "event", "subject", "value"];

    [Fact]
    public void WhenABareWordDiffersFromAVariableOnlyInCase_ThenTheHintNamesTheVariable()
    {
        // Act
        var hints = ExpressionHints.GetBareWordHints("/Door/OpenedAt > Now", Variables, readValue: null);

        // Assert
        Assert.Equal(["'Now' is read as the text \"Now\". The variable is written in lowercase: now."], hints.ToArray());
    }

    [Fact]
    public void WhenABareWordIsComparedWithANumber_ThenTheHintSaysItNeverMatches()
    {
        // Act
        var hints = ExpressionHints.GetBareWordHints("/Weather/Temperature > Limit", Variables, path => path == "/Weather/Temperature" ? 21.5 : null);

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
    [InlineData("/Weather/Temperature > -Limit")]
    [InlineData("/Weather/Temperature > -5")]
    [InlineData("/Weather/Temperature > -0.5")]
    public void WhenTheRightSideIsMeantAsWritten_ThenThereIsNoHint(string text)
    {
        // Act
        var hints = ExpressionHints.GetBareWordHints(text, Variables, path => path == "/Door/State" ? DayOfWeek.Monday : null);

        // Assert
        Assert.Empty(hints);
    }

    [Theory]
    [InlineData("/Door/OpenedAt < Now - 5m", "Now")]
    [InlineData("/a > Now/Offset", "Now")]
    [InlineData("Now - ./X > 5m", "Now")]
    [InlineData("/a > Now -(5m)", "Now")]
    [InlineData("/a > Time * 2", "Time")]
    [InlineData("/a > -Time", "Time")]
    [InlineData("-Now/Offset > 5m", "Now")]
    public void WhenAPathStartsWithAVariableInAnotherCase_ThenTheHintNamesTheVariable(string text, string word)
    {
        // Act
        var hints = ExpressionHints.GetBareWordHints(text, Variables, readValue: null);

        // Assert
        Assert.Equal([$"'{word}' is not the variable {word.ToLowerInvariant()}: variables are written in lowercase."], hints.ToArray());
    }

    [Fact]
    public void WhenAWordIsInParentheses_ThenTheHintIsStillGivenAndThePathIsRead()
    {
        // Arrange
        var readPaths = new List<string>();

        // Act
        var hints = ExpressionHints.GetBareWordHints("(/Weather/Temperature > Limit)", Variables, path =>
        {
            readPaths.Add(path);
            return 21.5;
        });

        // Assert
        Assert.Single(hints);
        Assert.Equal(["/Weather/Temperature"], readPaths);
        Assert.Equal(["'Now' is read as the text \"Now\". The variable is written in lowercase: now."], ExpressionHints.GetBareWordHints("(/a > Now)", Variables, readValue: null).ToArray());
    }

    [Fact]
    public void WhenAWordMatchesAVariableTheHostDoesNotDeclare_ThenThereIsNoVariableHint()
    {
        // Act
        var hints = ExpressionHints.GetBareWordHints("/Door/OpenedAt > Now and Now/Offset > 1", ["value", "subject"], readValue: null);

        // Assert
        Assert.Empty(hints);
    }
}
