using HomeBlaze.Storage.Blazor.Models;
using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Blazor.Tests;

public class MarkdownDecorationParserTests
{
    [Fact]
    public void Parse_EmptyContent_ReturnsEmptyList()
    {
        // Act
        var regions = MarkdownDecorationParser.Parse(null);

        // Assert
        Assert.Empty(regions);
    }

    [Fact]
    public void Parse_NoDecorableContent_ReturnsEmptyList()
    {
        // Arrange
        var content = "# Hello World\n\nSome plain text.";

        // Act
        var regions = MarkdownDecorationParser.Parse(content);

        // Assert
        Assert.Empty(regions);
    }

    [Fact]
    public void Parse_SingleExpression_ReturnsOneRegion()
    {
        // Arrange
        var content = "Temperature: {{ motor.Temperature }}";

        // Act
        var regions = MarkdownDecorationParser.Parse(content);

        // Assert
        var region = Assert.Single(regions);
        Assert.Equal(DecorationRegionType.Expression, region.Type);
        Assert.Equal("motor.Temperature", region.Name);
        Assert.Equal(1, region.StartLine);
        Assert.Equal(14, region.StartColumn);
        Assert.Equal(1, region.EndLine);
        Assert.Equal(37, region.EndColumn);
    }

    [Fact]
    public void Parse_MultipleExpressions_ReturnsMultipleRegions()
    {
        // Arrange
        var content = "Temp: {{ motor.Temperature }} Speed: {{ motor.Speed }}";

        // Act
        var regions = MarkdownDecorationParser.Parse(content);

        // Assert
        Assert.Equal(2, regions.Count);
        Assert.All(regions, r => Assert.Equal(DecorationRegionType.Expression, r.Type));
        Assert.Equal("motor.Temperature", regions[0].Name);
        Assert.Equal("motor.Speed", regions[1].Name);
    }

    [Fact]
    public void ContainsPosition_CursorInsideRegion_ReturnsTrue()
    {
        // Arrange
        var region = new DecorationRegion(
            StartLine: 1,
            StartColumn: 10,
            EndLine: 1,
            EndColumn: 30,
            Type: DecorationRegionType.Expression,
            Name: "test");

        // Act & Assert
        Assert.True(region.ContainsPosition(1, 10)); // At start
        Assert.True(region.ContainsPosition(1, 20)); // Middle
        Assert.True(region.ContainsPosition(1, 30)); // At end
    }

    [Fact]
    public void ContainsPosition_CursorOutsideRegion_ReturnsFalse()
    {
        // Arrange
        var region = new DecorationRegion(
            StartLine: 1,
            StartColumn: 10,
            EndLine: 1,
            EndColumn: 30,
            Type: DecorationRegionType.Expression,
            Name: "test");

        // Act & Assert
        Assert.False(region.ContainsPosition(1, 9));   // Before start
        Assert.False(region.ContainsPosition(1, 31));  // After end
        Assert.False(region.ContainsPosition(2, 15));  // Different line
    }

    [Fact]
    public void ContainsPosition_MultilineRegion_WorksCorrectly()
    {
        // Arrange - A subject block spanning lines 3-7
        var region = new DecorationRegion(
            StartLine: 3,
            StartColumn: 1,
            EndLine: 7,
            EndColumn: 4,
            Type: DecorationRegionType.SubjectBlock,
            Name: "mymotor");

        // Act & Assert
        Assert.True(region.ContainsPosition(3, 1));   // Start of block
        Assert.True(region.ContainsPosition(5, 10));  // Middle of block
        Assert.True(region.ContainsPosition(7, 3));   // End of block
        Assert.False(region.ContainsPosition(2, 50)); // Line before
        Assert.False(region.ContainsPosition(8, 1));  // Line after
    }

    [Fact]
    public void Parse_ExpressionWithWhitespace_TrimsPath()
    {
        // Arrange
        var content = "Value: {{   spaced.path   }}";

        // Act
        var regions = MarkdownDecorationParser.Parse(content);

        // Assert
        var region = Assert.Single(regions);
        Assert.Equal("spaced.path", region.Name);
    }

    [Fact]
    public void WhenContentHasWidgetBlock_ThenRegionCoversTheHeadLine()
    {
        // Arrange
        const string content = "Line 1\n\n:: Demo.Motor#mymotor Speed=3000\n::\n";

        // Act
        var region = Assert.Single(MarkdownDecorationParser.Parse(content));

        // Assert
        Assert.Equal(DecorationRegionType.SubjectBlock, region.Type);
        Assert.Equal("mymotor", region.Name);
        Assert.Equal(0, region.BlockIndex);
        Assert.Equal((3, 1, 3, 33), (region.StartLine, region.StartColumn, region.EndLine, region.EndColumn));
    }

    [Fact]
    public void WhenBlockHasNoId_ThenNameIsTheShortTypeName()
    {
        // Act
        var region = Assert.Single(MarkdownDecorationParser.Parse(":: Demo.Samples.Motor\n::\n"));

        // Assert
        Assert.Equal("Motor", region.Name);
    }

    [Fact]
    public void WhenBlockHasJsonFence_ThenRegionEndsAfterTheFence()
    {
        // Arrange
        const string content = ":: Demo.Grid Rows=1\n```json\n{ \"Cells\": [] }\n```\n::\n";

        // Act
        var region = Assert.Single(MarkdownDecorationParser.Parse(content));

        // Assert
        Assert.Equal(1, region.StartLine);
        Assert.Equal(4, region.EndLine);
    }

    [Fact]
    public void WhenBlocksAreNested_ThenRegionsDoNotOverlap()
    {
        // Arrange
        const string content = ":: Demo.Card Title=A\ntext\n\n::: Demo.Widget Path=/x\n:::\n::\n";

        // Act
        var regions = MarkdownDecorationParser.Parse(content);

        // Assert
        Assert.Equal(2, regions.Count);
        Assert.True(regions[0].ContainsPosition(1, 5));
        Assert.False(regions[0].ContainsPosition(4, 5));
        Assert.True(regions[1].ContainsPosition(4, 5));
    }

    [Theory]
    [InlineData("::Demo.Motor Speed=1", 21)]
    [InlineData(":: Demo.Motor Speed=1", 22)]
    [InlineData("::::  Demo.Motor Speed=1", 25)]
    public void WhenHeadLineHasAnyFenceLengthAndSpacing_ThenTheRegionCoversTheWholeHeadLine(string headLine, int endColumn)
    {
        // Arrange
        var content = $"text\n\n{headLine}\n{headLine[..headLine.IndexOf('D')].TrimEnd()}\n";

        // Act
        var region = Assert.Single(MarkdownDecorationParser.Parse(content));

        // Assert
        Assert.Equal((3, 1, 3, endColumn), (region.StartLine, region.StartColumn, region.EndLine, region.EndColumn));
        Assert.Equal("Motor", region.Name);
    }

    [Fact]
    public void WhenSlotBlockHasNoSubject_ThenItGetsNoRegion()
    {
        // Arrange
        const string content = ":: Demo.Card\n::: Footer\ntext\n:::\n::\n";

        // Act
        var regions = MarkdownDecorationParser.Parse(content, _ => null);

        // Assert
        Assert.Equal("Card", Assert.Single(regions).Name);
    }

    [Fact]
    public void WhenBlockCreatedASubject_ThenItsRegionCarriesTheSubject()
    {
        // Arrange
        var subject = new HtmlSegment("subject of the second block");
        const string content = ":: Demo.Motor#first\n::\n\n:: Demo.Motor#second\n::\n";

        // Act
        var regions = MarkdownDecorationParser.Parse(content, blockIndex => blockIndex == 1 ? subject : null);

        // Assert
        Assert.Equal(2, regions.Count);
        Assert.Null(regions[0].Subject);
        Assert.Same(subject, regions[1].Subject);
    }

    [Fact]
    public void WhenSlotBlockHasSubject_ThenItGetsARegionWithTheSubject()
    {
        // Arrange
        var subject = new HtmlSegment("subject of the slot");
        const string content = ":: Demo.Card\n::: Footer\ntext\n:::\n::\n";

        // Act
        var regions = MarkdownDecorationParser.Parse(content, blockIndex => blockIndex == 1 ? subject : null);

        // Assert
        Assert.Equal(2, regions.Count);
        Assert.Equal("Footer", regions[1].Name);
        Assert.Same(subject, regions[1].Subject);
        Assert.Equal(1, regions[1].BlockIndex);
    }

    [Theory]
    [InlineData(":: Demo.Motor Speed=1\ntext\n")]
    [InlineData(":: Demo.Motor Speed=1\ntext")]
    [InlineData(":: Demo.Motor Speed=1")]
    public void WhenBlockHasNoClosingLine_ThenItsRegionCoversTheHeadLine(string content)
    {
        // Act
        var region = Assert.Single(MarkdownDecorationParser.Parse(content));

        // Assert
        Assert.Equal(DecorationRegionType.SubjectBlock, region.Type);
        Assert.Equal("Motor", region.Name);
        Assert.Equal((1, 1, 1, 22), (region.StartLine, region.StartColumn, region.EndLine, region.EndColumn));
    }

    [Theory]
    [InlineData(":: Demo.Motor Speed=\"1\ntext\n::\n")]
    [InlineData("::\ntext\n")]
    public void WhenBlockIsInvalid_ThenItGetsNoRegion(string content)
    {
        // Act
        var regions = MarkdownDecorationParser.Parse(content);

        // Assert
        Assert.Empty(regions);
    }

    [Fact]
    public void WhenExpressionsAreInsideCode_ThenOnlyTheOneOutsideCodeGetsARegion()
    {
        // Arrange
        const string content = "Live {{ a/B }} value\n\nWrite `{{ c/D }}` here\n\n```\n{{ e/F }}\n```\n";

        // Act
        var region = Assert.Single(MarkdownDecorationParser.Parse(content));

        // Assert
        Assert.Equal(DecorationRegionType.Expression, region.Type);
        Assert.Equal("a/B", region.Name);
        Assert.Equal((1, 6, 1, 15), (region.StartLine, region.StartColumn, region.EndLine, region.EndColumn));
    }

    [Fact]
    public void WhenInlineCodeHasOnlyOpeningBraces_ThenTheExpressionAfterItGetsARegion()
    {
        // Arrange
        const string content = "Start with `{{` to show {{ a/B }} here";

        // Act
        var region = Assert.Single(MarkdownDecorationParser.Parse(content));

        // Assert
        Assert.Equal("a/B", region.Name);
        Assert.Equal((1, 25, 1, 34), (region.StartLine, region.StartColumn, region.EndLine, region.EndColumn));
    }

    [Fact]
    public void WhenClosingBracesAreInsideInlineCode_ThenTheyCloseNoExpression()
    {
        // Act
        var regions = MarkdownDecorationParser.Parse("Open with {{ and close with `}}` here");

        // Assert
        Assert.Empty(regions);
    }
}
