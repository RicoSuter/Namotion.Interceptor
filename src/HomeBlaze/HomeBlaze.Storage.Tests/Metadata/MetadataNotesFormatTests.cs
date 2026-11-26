using HomeBlaze.Storage.Metadata;

namespace HomeBlaze.Storage.Tests.Metadata;

public class MetadataNotesFormatTests
{
    private readonly MetadataNotesFormat _format = new();

    [Fact]
    public void WhenNotesFollowTheRules_ThenTheyRenderAsHtmlWithoutError()
    {
        // Act
        var error = _format.GetError("Some **bold** text");
        var html = _format.ToHtml("Some **bold** text");

        // Assert
        Assert.Null(error);
        Assert.Contains("<strong>bold</strong>", html);
    }

    [Fact]
    public void WhenNotesHoldHtml_ThenTheyHaveAnErrorAndNoHtml()
    {
        // Act
        var error = _format.GetError("<b>bold</b>");
        var html = _format.ToHtml("<b>bold</b>");

        // Assert
        Assert.NotNull(error);
        Assert.Null(html);
    }

    [Fact]
    public void WhenTheMaximumSizeIsRead_ThenItIsThatOfMetadataNotes()
    {
        // Act & Assert
        Assert.Equal(MetadataNotes.MaxSize, _format.MaxSize);
    }
}
