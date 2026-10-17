using HomeBlaze.Components.Controls;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class GridRowsTests
{
    [Fact]
    public void WhenNoAspectRatioIsSet_ThenRowsShareTheHeight()
    {
        // Act
        var template = GridRows.GetTemplate(3, 4, null, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal("repeat(3, 1fr)", template);
    }

    [Fact]
    public void WhenAspectRatioIsSquare_ThenARowIsAsHighAsAColumnIsWide()
    {
        // Act
        var template = GridRows.GetTemplate(5, 9, "1:1", out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal("repeat(5, calc((100cqw - 128px) / 9 * 1 / 1))", template);
    }

    [Fact]
    public void WhenAspectRatioIsWide_ThenARowIsLowerThanAColumnIsWide()
    {
        // Act
        var template = GridRows.GetTemplate(2, 2, "4:3", out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal("repeat(2, calc((100cqw - 16px) / 2 * 3 / 4))", template);
    }

    [Theory]
    [InlineData("square")]
    [InlineData("1:0")]
    [InlineData("-1:1")]
    public void WhenAspectRatioIsInvalid_ThenItIsAnErrorThatNamesTheValue(string value)
    {
        // Act
        var template = GridRows.GetTemplate(2, 2, value, out var error);

        // Assert
        Assert.Null(template);
        Assert.Equal($"CellAspectRatio must look like 1:1: {value}", error);
    }
}
