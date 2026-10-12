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
}
