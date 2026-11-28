namespace Namotion.Matter.Tests;

public class AttributePathTests
{
    [Fact]
    public void WhenParsingValidPath_ThenPartsAreRead()
    {
        // Act
        var path = AttributePath.Parse("1/6/16384");

        // Assert
        Assert.Equal(new AttributePath(1, 6, 16384), path);
    }

    [Fact]
    public void WhenFormatting_ThenSlashSeparatedDecimalsAreWritten()
    {
        // Arrange
        var path = new AttributePath(65535, 4294967295, 0);

        // Act
        var text = path.ToString();

        // Assert
        Assert.Equal("65535/4294967295/0", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1/6")]
    [InlineData("1/6/0/0")]
    [InlineData("*/6/0")]
    [InlineData("65536/6/0")]
    [InlineData("-1/6/0")]
    [InlineData(" 1/6/0")]
    public void WhenParsingInvalidPath_ThenTryParseFails(string text)
    {
        // Act
        var success = AttributePath.TryParse(text, out _);

        // Assert
        Assert.False(success);
    }

    [Fact]
    public void WhenParsingInvalidPath_ThenParseThrowsFormatException()
    {
        // Act & Assert
        Assert.Throws<FormatException>(() => AttributePath.Parse("1/6"));
    }
}
