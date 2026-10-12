using HomeBlaze.Storage.Pages;
using Xunit;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownExpressionTextTests
{
    [Theory]
    [InlineData("motor/Speed", "motor/Speed", ExpressionFormat.Display)]
    [InlineData("motor/Speed | display", "motor/Speed", ExpressionFormat.Display)]
    [InlineData("motor/Speed | raw", "motor/Speed", ExpressionFormat.Raw)]
    [InlineData("motor/Speed|RAW", "motor/Speed", ExpressionFormat.Raw)]
    [InlineData("motor/Speed  |  Raw  ", "motor/Speed", ExpressionFormat.Raw)]
    public void WhenFormatIsKnownOrMissing_ThenPathAndFormatAreSplit(string text, string path, ExpressionFormat format)
    {
        // Act
        var expression = MarkdownExpressionText.Parse(text);

        // Assert
        Assert.Equal(path, expression.Path);
        Assert.Equal(format, expression.Format);
        Assert.Null(expression.Error);
    }

    [Fact]
    public void WhenFormatIsUnknown_ThenErrorNamesIt()
    {
        // Act
        var expression = MarkdownExpressionText.Parse("motor/Speed | rounded");

        // Assert
        Assert.Equal("motor/Speed", expression.Path);
        Assert.Equal("Unknown format 'rounded'", expression.Error);
    }

    [Fact]
    public void WhenSeparatorIsInsideBrackets_ThenItBelongsToThePath()
    {
        // Act
        var expression = MarkdownExpressionText.Parse("/Devices[a|b]/Speed");

        // Assert
        Assert.Equal("/Devices[a|b]/Speed", expression.Path);
        Assert.Equal(ExpressionFormat.Display, expression.Format);
        Assert.Null(expression.Error);
    }

    [Fact]
    public void WhenSeparatorFollowsBrackets_ThenTheLastOneOutsideSplits()
    {
        // Act
        var expression = MarkdownExpressionText.Parse("/Devices[a|b]/Speed | raw");

        // Assert
        Assert.Equal("/Devices[a|b]/Speed", expression.Path);
        Assert.Equal(ExpressionFormat.Raw, expression.Format);
    }

    [Fact]
    public void WhenSeparatorIsEscapedForATable_ThenTheBackslashIsNotPartOfThePath()
    {
        // Act
        var expression = MarkdownExpressionText.Parse(@"motor/Speed \| raw");

        // Assert
        Assert.Equal("motor/Speed", expression.Path);
        Assert.Equal(ExpressionFormat.Raw, expression.Format);
    }
}
