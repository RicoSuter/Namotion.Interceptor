using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class HtmlSegmentTests
{
    [Fact]
    public void WhenTheHtmlHasNoBlockStarts_ThenItIsOneBlockOfTheSameString()
    {
        // Arrange
        var html = "<p>A</p>\n";

        // Act
        var segment = new HtmlSegment(html);

        // Assert
        Assert.Same(html, Assert.Single(segment.Blocks));
        Assert.Same(html, segment.Html);
        Assert.False(segment.StartsWithBlock);
        Assert.False(segment.EndsWithBlock);
    }

    [Fact]
    public void WhenBlocksStartAtTheStartAndTheEnd_ThenTheBlockIsTheSameStringAndNoEmptyBlockIsKept()
    {
        // Arrange
        var html = "<p>A</p>\n";

        // Act
        var segment = new HtmlSegment(html, blockStarts: [0, html.Length]);

        // Assert
        Assert.Same(html, Assert.Single(segment.Blocks));
        Assert.True(segment.StartsWithBlock);
        Assert.True(segment.EndsWithBlock);
    }

    [Fact]
    public void WhenABlockStartsInside_ThenTheHtmlIsCutThere()
    {
        // Act
        var segment = new HtmlSegment("<p>A</p>\n<p>B</p>\n", blockStarts: [9]);

        // Assert
        Assert.Equal(["<p>A</p>\n", "<p>B</p>\n"], segment.Blocks.ToArray());
        Assert.Equal("<p>A</p>\n<p>B</p>\n", segment.Html);
        Assert.False(segment.StartsWithBlock);
        Assert.False(segment.EndsWithBlock);
    }
}
