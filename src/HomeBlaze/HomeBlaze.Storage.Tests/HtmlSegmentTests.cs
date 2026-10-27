using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class HtmlSegmentTests
{
    [Fact]
    public void WhenABlockBetweenTwoBlockStartsIsTakenTwice_ThenItIsTheSameString()
    {
        // Arrange
        var segment = new HtmlSegment("<p>A</p>\n<p>B</p>\n<p>C</p>\n", [9, 18]);

        // Act
        var first = segment.GetCompleteBlock(0);
        var second = segment.GetCompleteBlock(0);

        // Assert
        Assert.Equal("<p>B</p>\n", first);
        Assert.Same(first, second);
    }
}
