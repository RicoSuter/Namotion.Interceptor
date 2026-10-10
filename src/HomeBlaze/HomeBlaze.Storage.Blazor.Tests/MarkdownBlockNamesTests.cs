using HomeBlaze.Storage.Blazor.Models;

namespace HomeBlaze.Storage.Blazor.Tests;

public class MarkdownBlockNamesTests
{
    private const string Page =
        "# Page\n\n:::::Demo.Card#card\n::::Footer\n:::Demo.Motor#inner speed=1\n:::\n::::\n:::::\n\n:::Demo.Motor speed=2\n:::\n";

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
    [InlineData("Card")]
    public void WhenNameIsValidAndNotUsed_ThenThereIsNoError(string name)
    {
        // Act
        var error = MarkdownBlockNames.GetError(name, MarkdownBlockNames.GetUsedNames(Page));

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData("Living Room")]
    [InlineData("2ndfloor")]
    [InlineData("a.b")]
    [InlineData("küche")]
    public void WhenNameIsNoValidBlockName_ThenTheErrorSaysWhatAValidNameIs(string name)
    {
        // Act
        var error = MarkdownBlockNames.GetError(name, MarkdownBlockNames.GetUsedNames(Page));

        // Assert
        Assert.Equal($"'{name}' cannot be used as a name on a page. {MarkdownBlockNames.Rule}", error);
    }

    [Theory]
    [InlineData("card")]
    [InlineData("inner")]
    public void WhenNameIsUsedOnThePage_ThenTheErrorSaysSo(string name)
    {
        // Act
        var error = MarkdownBlockNames.GetError(name, MarkdownBlockNames.GetUsedNames(Page));

        // Assert
        Assert.Equal($"The name '{name}' is already used on this page.", error);
    }
}
