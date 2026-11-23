using HomeBlaze.Components.Dialogs;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SubjectNameOptionsTests
{
    private static SubjectNameOptions CreateOptions(string label = "File name", params string[] existingNames)
    {
        return new SubjectNameOptions(label, ".json", existingNames);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void WhenNameIsEmpty_ThenItIsRequired(string? name)
    {
        // Arrange
        var options = CreateOptions();

        // Act
        var error = options.Validate(name);

        // Assert
        Assert.Equal("File name is required", error);
    }

    [Fact]
    public void WhenNameContainsACharacterThatIsInvalidInAFileName_ThenItIsRejected()
    {
        // Arrange
        var options = CreateOptions();

        // Act
        var error = options.Validate("invalid/name");

        // Assert
        Assert.Equal("File name contains invalid characters", error);
    }

    [Theory]
    [InlineData("history")]
    [InlineData("HISTORY")]
    [InlineData(" History ")]
    [InlineData("motor")]
    [InlineData("Motor.json")]
    public void WhenExistingNameMatchesWithOrWithoutSuffixIgnoringCase_ThenNameIsTaken(string name)
    {
        // Arrange
        var options = CreateOptions("File name", "History", "Motor.json", "Dashboard.md");

        // Act
        var error = options.Validate(name);

        // Assert
        Assert.Equal("This file name is already used", error);
    }

    [Theory]
    [InlineData("Dashboard")]
    [InlineData("Other")]
    [InlineData("History2")]
    [InlineData(" Valid name ")]
    public void WhenNameIsFreeAndUsableAsAFileName_ThenItIsValid(string name)
    {
        // Arrange
        var options = CreateOptions("File name", "History", "Motor.json", "Dashboard.md");

        // Act
        var error = options.Validate(name);

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData("Block name", "Block name is required", "Block name contains invalid characters", "This block name is already used")]
    [InlineData("URL", "URL is required", "URL contains invalid characters", "This URL is already used")]
    public void WhenNameIsInvalid_ThenTheMessageNamesTheFieldByItsLabel(
        string label, string expectedRequiredError, string expectedInvalidCharactersError, string expectedTakenError)
    {
        // Arrange
        var options = CreateOptions(label, "Taken");

        // Act
        var requiredError = options.Validate("");
        var invalidCharactersError = options.Validate("a/b");
        var takenError = options.Validate("taken");

        // Assert
        Assert.Equal(expectedRequiredError, requiredError);
        Assert.Equal(expectedInvalidCharactersError, invalidCharactersError);
        Assert.Equal(expectedTakenError, takenError);
    }

    [Theory]
    [InlineData(" two words ")]
    [InlineData("two/words")]
    public void WhenNameDoesNotFitTheRule_ThenTheErrorOfTheRuleIsReturnedForTheTrimmedName(string name)
    {
        // Arrange
        var options = CreateOptions() with
        {
            Rule = trimmedName => trimmedName.All(char.IsLetter) ? null : $"'{trimmedName}' has more than letters"
        };

        // Act
        var error = options.Validate(name);

        // Assert
        Assert.Equal($"'{name.Trim()}' has more than letters", error);
    }

    [Fact]
    public void WhenNameFitsTheRule_ThenTheOtherChecksStillApply()
    {
        // Arrange
        var options = CreateOptions("File name", "History") with { Rule = _ => null };

        // Act
        var takenError = options.Validate("history");
        var invalidCharactersError = options.Validate("invalid/name");
        var validError = options.Validate("Other");

        // Assert
        Assert.Equal("This file name is already used", takenError);
        Assert.Equal("File name contains invalid characters", invalidCharactersError);
        Assert.Null(validError);
    }

    [Fact]
    public void WhenNameIsEmpty_ThenTheRuleIsNotAsked()
    {
        // Arrange
        var options = CreateOptions() with { Rule = _ => "The rule was asked" };

        // Act
        var error = options.Validate("  ");

        // Assert
        Assert.Equal("File name is required", error);
    }

    [Fact]
    public void WhenClassNameIsFree_ThenItIsSuggested()
    {
        // Arrange
        var options = CreateOptions("File name", "Other", "Dashboard.md");

        // Act
        var name = options.SuggestName(typeof(SubjectNameOptionsTests));

        // Assert
        Assert.Equal("SubjectNameOptionsTests", name);
    }

    [Fact]
    public void WhenClassNameIsTaken_ThenTheNextFreeNumberIsAppended()
    {
        // Arrange
        var options = CreateOptions("File name", "SubjectNameOptionsTests", "subjectnameoptionstests2.json");

        // Act
        var name = options.SuggestName(typeof(SubjectNameOptionsTests));

        // Assert
        Assert.Equal("SubjectNameOptionsTests3", name);
        Assert.Null(options.Validate(name));
    }

    [Fact]
    public void WhenThePreferredNameIsValid_ThenItIsSuggested()
    {
        // Arrange
        var options = new SubjectNameOptions("File name", ".json", ["Other.json"]);

        // Act
        var name = options.SuggestName("  Philips Hue - 1A2B3C ", typeof(SubjectNameOptionsTests));

        // Assert
        Assert.Equal("Philips Hue - 1A2B3C", name);
    }

    [Fact]
    public void WhenThePreferredNameIsTaken_ThenTheNextFreeNumberIsAppended()
    {
        // Arrange
        var options = new SubjectNameOptions("File name", ".json", ["FRITZ!Repeater 1200 AX.json", "fritz!repeater 1200 ax 2"]);

        // Act
        var name = options.SuggestName("FRITZ!Repeater 1200 AX", typeof(SubjectNameOptionsTests));

        // Assert
        Assert.Equal("FRITZ!Repeater 1200 AX 3", name);
        Assert.Null(options.Validate(name));
    }

    [Fact]
    public void WhenThePreferredNameIsInvalid_ThenTheTypeNameIsSuggested()
    {
        // Arrange
        var options = new SubjectNameOptions("File name", ".json", []);

        // Act
        var name = options.SuggestName("Living/room", typeof(SubjectNameOptionsTests));

        // Assert
        Assert.Equal(nameof(SubjectNameOptionsTests), name);
    }

    [Fact]
    public void WhenTheNumberedPreferredNameDoesNotFitTheRule_ThenTheTypeNameIsSuggested()
    {
        // Arrange
        var options = new SubjectNameOptions("File name", ".json", ["Kitchen.json"])
        {
            Rule = trimmedName => trimmedName.All(char.IsLetter) ? null : "Only letters"
        };

        // Act
        var name = options.SuggestName("Kitchen", typeof(SubjectNameOptionsTests));

        // Assert
        Assert.Equal(nameof(SubjectNameOptionsTests), name);
    }

    [Theory]
    [InlineData("Living\nroom")]
    [InlineData("Living\troom")]
    [InlineData("Living\u0007room")]
    [InlineData("Living\u202Emoor")]
    [InlineData("Living\u200Broom")]
    [InlineData(".hidden")]
    [InlineData(" .hidden ")]
    public void WhenThePreferredNameIsNotPlain_ThenTheTypeNameIsSuggested(string preferredName)
    {
        // Arrange
        var options = new SubjectNameOptions("File name", ".json", []);

        // Act
        var name = options.SuggestName(preferredName, typeof(SubjectNameOptionsTests));

        // Assert
        Assert.Equal(nameof(SubjectNameOptionsTests), name);
    }

    [Fact]
    public void WhenThePreferredNameHasADotInside_ThenItIsSuggested()
    {
        // Arrange
        var options = new SubjectNameOptions("File name", ".json", []);

        // Act
        var name = options.SuggestName("shelly1.kitchen", typeof(SubjectNameOptionsTests));

        // Assert
        Assert.Equal("shelly1.kitchen", name);
    }
}
