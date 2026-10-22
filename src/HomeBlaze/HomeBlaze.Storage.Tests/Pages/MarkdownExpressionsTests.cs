using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownExpressionsTests
{
    [Theory]
    [InlineData("motor/Speed", "motor/Speed", 0)]
    [InlineData("motor/Speed | display", "motor/Speed", 1)]
    [InlineData("motor/Speed | raw", "motor/Speed", 1)]
    [InlineData("motor/Speed|RAW", "motor/Speed", 1)]
    [InlineData("motor/Speed  |  Raw  ", "motor/Speed", 1)]
    [InlineData("/Devices[a|b]/Speed", "/Devices[a|b]/Speed", 0)]
    [InlineData("/Devices[a|b]/Speed | raw", "/Devices[a|b]/Speed", 1)]
    [InlineData(@"motor/Speed \| raw", "motor/Speed", 1)]
    public void WhenTextIsAPathWithAFormat_ThenItParsesAsBefore(string text, string path, int pipeCount)
    {
        // Act
        var expression = MarkdownExpressions.Parse(text, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(path, expression!.SinglePath);
        Assert.Equal(pipeCount, expression.Pipes.Length);
    }

    [Theory]
    [InlineData("/Docs/My Page.md/Title", "/Docs/My%20Page.md/Title")]
    [InlineData("../My  Page.md/Title | raw", "../My%20%20Page.md/Title")]
    public void WhenPathHasASpaceInASegment_ThenItIsReadPercentEncoded(string text, string path)
    {
        // Act
        var expression = MarkdownExpressions.Parse(text, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(path, expression!.SinglePath);
    }

    [Theory]
    [InlineData("meter/Power > 1000")]
    [InlineData("meter/IsOn and not meter/Power < 5")]
    [InlineData(@"meter/IsOn \|\| meter/Power == 5")]
    public void WhenTextIsACondition_ThenItParses(string text)
    {
        // Act
        var expression = MarkdownExpressions.Parse(text, out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(expression);
    }

    [Theory]
    [InlineData("motor/Speed | rounded", "Unknown pipe 'rounded'")]
    [InlineData("motor/Speed | raw:2", "The pipe 'raw' takes no arguments")]
    [InlineData("value", "'value' is not available in the text of a page")]
    [InlineData("subject/Power > 5", "'subject' is not available in the text of a page")]
    [InlineData("a - b", "arithmetic is not supported at position 2")]
    [InlineData("a = b", "use '==' for equality at position 2")]
    public void WhenTextIsWrongForAPage_ThenErrorIsShown(string text, string expectedError)
    {
        // Act
        var expression = MarkdownExpressions.Parse(text, out var error);

        // Assert
        Assert.Null(expression);
        Assert.Equal(expectedError, error);
    }
}
