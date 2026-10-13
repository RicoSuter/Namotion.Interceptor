using HomeBlaze.Storage.Blazor.Models;

namespace HomeBlaze.Storage.Blazor.Tests;

public class MarkdownBlockNamesTests
{
    private const string Page =
        "# Page\n\n:: Demo.Card#card\n::: Footer\n:::: Demo.Motor#inner Speed=1\n::::\n:::\n::\n\n:: Demo.Motor Speed=2\n::\n";

    [Fact]
    public void WhenPageHasNamedBlocksAtAnyDepth_ThenTheirNamesAreUsed()
    {
        // Act
        var usedNames = MarkdownBlockNames.GetUsedNames(Page);

        // Assert
        Assert.Equal(["card", "inner"], usedNames.Order());
    }

    [Theory]
    [InlineData("livingroom")]
    [InlineData("_living-room_2")]
    [InlineData("Motor")]
    public void WhenNameIsValidAndNotUsed_ThenThereIsNoError(string name)
    {
        // Arrange
        var options = MarkdownBlockNames.CreateNameOptions(Page);

        // Act
        var error = options.Validate(name);

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData("Living Room")]
    [InlineData("2ndfloor")]
    [InlineData("a.b")]
    [InlineData("a/b")]
    [InlineData("küche")]
    public void WhenNameIsNoValidBlockName_ThenTheErrorSaysWhatAValidNameIs(string name)
    {
        // Arrange
        var options = MarkdownBlockNames.CreateNameOptions(Page);

        // Act
        var error = options.Validate(name);

        // Assert
        Assert.Equal($"'{name}' cannot be used as a name on a page. {MarkdownBlockNames.Rule}", error);
    }

    [Theory]
    [InlineData("card")]
    [InlineData("inner")]
    [InlineData("Card")]
    public void WhenNameIsUsedOnThePageIgnoringCase_ThenTheErrorSaysSo(string name)
    {
        // Arrange
        var options = MarkdownBlockNames.CreateNameOptions(Page);

        // Act
        var error = options.Validate(name);

        // Assert
        Assert.Equal("This block name is already used", error);
    }

    [Fact]
    public void WhenNameIsEmpty_ThenTheErrorNamesTheFieldAsBlockName()
    {
        // Arrange
        var options = MarkdownBlockNames.CreateNameOptions(Page);

        // Act
        var error = options.Validate(" ");

        // Assert
        Assert.Equal("Block name is required", error);
    }

    [Fact]
    public void WhenTheTypeNameIsUsedOnThePage_ThenTheSuggestedNameGetsANumberAndIsValid()
    {
        // Arrange
        var options = MarkdownBlockNames.CreateNameOptions(
            "# Page\n\n:: Demo.Motor#markdownblocknamestests Speed=1\n::\n");

        // Act
        var name = options.SuggestName(typeof(MarkdownBlockNamesTests));

        // Assert
        Assert.Equal("MarkdownBlockNamesTests2", name);
        Assert.Null(options.Validate(name));
    }
}
