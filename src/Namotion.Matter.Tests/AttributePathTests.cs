using System.Text;

namespace Namotion.Matter.Tests;

public class AttributePathTests
{
    [Theory]
    [InlineData("1/6/16384", 1, 6, 16384)]
    [InlineData("01/6/0", 1, 6, 0)]
    [InlineData("65535/4294967295/4294967295", 65535, 4294967295, 4294967295)]
    public void WhenParsingValidPath_ThenPartsAreRead(string text, int endpoint, uint cluster, uint attribute)
    {
        // Act
        var success = AttributePath.TryParse(Encoding.UTF8.GetBytes(text), out var path);

        // Assert
        Assert.True(success);
        Assert.Equal(new AttributePath((ushort)endpoint, cluster, attribute), path);
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
    [InlineData("1//0")]
    [InlineData("*/6/0")]
    [InlineData("65536/6/0")]
    [InlineData("1/4294967296/0")]
    [InlineData("-1/6/0")]
    [InlineData("+1/6/0")]
    [InlineData(" 1/6/0")]
    [InlineData("1/6/0 ")]
    [InlineData("1/6/0x1")]
    public void WhenParsingInvalidPath_ThenTryParseFails(string text)
    {
        // Act
        var success = AttributePath.TryParse(Encoding.UTF8.GetBytes(text), out var path);

        // Assert
        Assert.False(success);
        Assert.Equal(default, path);
    }
}
