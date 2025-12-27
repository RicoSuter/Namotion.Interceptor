using HomeBlaze.Storage.Blazor.Models;

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
    public void Parse_SubjectBlock_ReturnsOneRegion()
    {
        // Arrange
        var content = """
            # Motor Status

            ```subject(mymotor)
            {
              "$type": "HomeBlaze.Devices.Motor"
            }
            ```
            """;

        // Act
        var regions = MarkdownDecorationParser.Parse(content);

        // Assert
        var region = Assert.Single(regions);
        Assert.Equal(DecorationRegionType.SubjectBlock, region.Type);
        Assert.Equal("mymotor", region.Name);
        Assert.Equal(3, region.StartLine);
        Assert.Equal(1, region.StartColumn);
    }

    [Fact]
    public void Parse_MixedContent_ReturnsBothTypes()
    {
        // Arrange
        var content = """
            # Motor Dashboard

            Current temperature: {{ mymotor.Temperature }}

            ```subject(mymotor)
            {
              "$type": "HomeBlaze.Devices.Motor"
            }
            ```

            Speed: {{ mymotor.Speed }}
            """;

        // Act
        var regions = MarkdownDecorationParser.Parse(content);

        // Assert
        Assert.Equal(3, regions.Count);
        Assert.Equal(2, regions.Count(r => r.Type == DecorationRegionType.Expression));
        Assert.Single(regions, r => r.Type == DecorationRegionType.SubjectBlock);
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
    public void Parse_MultilineSubjectBlock_CorrectEndPosition()
    {
        // Arrange
        var content = "Line 1\nLine 2\n```subject(test)\n{\n  \"type\": \"Test\"\n}\n```\nLine 8";

        // Act
        var regions = MarkdownDecorationParser.Parse(content);

        // Assert
        var region = Assert.Single(regions);
        Assert.Equal(3, region.StartLine);
        Assert.Equal(7, region.EndLine);
    }
}
