using System.Collections.Immutable;
using HomeBlaze.Components.Inputs;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ExpressionHintsTests
{
    private static readonly ImmutableArray<string> Variables = ["now", "time", "event", "subject", "value"];

    [Theory]
    [InlineData("/Door/OpenedAt > Now")]
    [InlineData("(/a > Now)")]
    public void WhenABareWordDiffersFromAVariableOnlyInCase_ThenTheHintNamesTheVariable(string text)
    {
        // Act
        var hints = GetShownHints(text, Variables, readValue: null);

        // Assert
        Assert.Equal(["'Now' is read as the text \"Now\". The variable is written in lowercase: now."], hints.ToArray());
    }

    [Fact]
    public void WhenABareWordIsComparedWithANumber_ThenTheHintSaysItNeverMatches()
    {
        // Act
        var hints = GetShownHints("/Weather/Temperature > Limit", Variables, path => path == "/Weather/Temperature" ? 21.5 : null);

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
        var hints = GetShownHints(text, Variables, path => path == "/Door/State" ? DayOfWeek.Monday : null);

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
        var withoutValues = GetShownHints(text, Variables, readValue: null);
        var withoutValueAtThePath = GetShownHints(text, Variables, _ => null);

        // Assert
        Assert.Equal([$"'{word}' is not the variable {word.ToLowerInvariant()}: variables are written in lowercase."], withoutValues.ToArray());
        Assert.Equal(withoutValues, withoutValueAtThePath);
    }

    [Theory]
    [InlineData("Value > 5", "Value")]
    [InlineData("Subject/Name == 'x'", "Subject/Name")]
    [InlineData("/a > -Time", "Time")]
    [InlineData("-Now/Offset > 5m", "Now/Offset")]
    public void WhenAPathThatStartsWithAVariableInAnotherCaseLeadsToAValue_ThenThereIsNoHint(string text, string path)
    {
        // Act
        var hints = GetShownHints(text, Variables, read => read == path ? 1 : null);

        // Assert
        Assert.Empty(hints);
    }

    [Fact]
    public void WhenAWordIsInParentheses_ThenTheHintIsStillGivenAndThePathIsRead()
    {
        // Arrange
        var readPaths = new List<string>();

        // Act
        var hints = GetShownHints("(/Weather/Temperature > Limit)", Variables, path =>
        {
            readPaths.Add(path);
            return 21.5;
        });

        // Assert
        Assert.Single(hints);
        Assert.Equal(["/Weather/Temperature"], readPaths);
    }

    [Fact]
    public void WhenAWordMatchesAVariableTheHostDoesNotDeclare_ThenThereIsNoVariableHint()
    {
        // Act
        var hints = GetShownHints("/Door/OpenedAt > Now and Now/Offset > 1", ["value", "subject"], readValue: null);

        // Assert
        Assert.Empty(hints);
    }

    // The hints as the field shows them with the values that readValue gives, or without values when it is null.
    private static List<string> GetShownHints(string text, ImmutableArray<string> variables, Func<string, object?>? readValue) =>
    [
        .. ExpressionHints.Find(text, variables)
            .Where(hint => hint.IsShown(hint.Path is null ? null : readValue?.Invoke(hint.Path), readValue is not null))
            .Select(hint => hint.Text)
    ];
}
