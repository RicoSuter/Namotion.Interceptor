using HomeBlaze.Components.Inputs;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class MarkdownFieldModelTests
{
    [Fact]
    public void WhenTheTextHasMultiByteCharacters_ThenTheSizeCountsUtf8Bytes()
    {
        // Act
        var model = MarkdownFieldModel.Create("Tür", toHtml: text => text, getError: _ => null, maxSize: 10);

        // Assert
        Assert.Equal(4, model.Size);
        Assert.False(model.IsTooLarge);
    }

    [Fact]
    public void WhenTheTextIsLargerThanTheMaximum_ThenItIsTooLargeAndHasAnError()
    {
        // Act
        var model = MarkdownFieldModel.Create("12345", toHtml: text => text, getError: _ => null, maxSize: 4);

        // Assert
        Assert.True(model.IsTooLarge);
        Assert.Equal("The text is 5 bytes, more than the 4 allowed.", model.Error);
    }

    [Fact]
    public void WhenTheRendererReportsAnError_ThenTheModelHasItAndNoHtml()
    {
        // Act
        var model = MarkdownFieldModel.Create("<b>x</b>", toHtml: _ => null, getError: _ => "notes may not hold HTML.", maxSize: null);

        // Assert
        Assert.Equal("notes may not hold HTML.", model.Error);
        Assert.Null(model.Html);
    }

    [Fact]
    public void WhenTheTextIsEmpty_ThenTheModelHasNoHtmlAndNoError()
    {
        // Act
        var model = MarkdownFieldModel.Create("", toHtml: _ => "<p></p>", getError: _ => "never", maxSize: 4);

        // Assert
        Assert.Null(model.Html);
        Assert.Null(model.Error);
        Assert.Equal(0, model.Size);
    }
}
