using HomeBlaze.Components.Controls;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class CanvasExtentTests
{
    private static CanvasNode CreateNode(int x, int width) => new() { X = x, Width = width };

    [Fact]
    public void WhenWidthIsSet_ThenItIsTheDesignWidth()
    {
        // Act
        var width = CanvasExtent.GetDesignWidth(1244, [CreateNode(0, 2000)]);

        // Assert
        Assert.Equal(1244, width);
    }

    [Fact]
    public void WhenWidthIsNotSet_ThenTheRightEdgeOfTheRightmostNodeIsTheDesignWidth()
    {
        // Act
        var width = CanvasExtent.GetDesignWidth(null, [CreateNode(0, 124), CreateNode(1120, 124), CreateNode(500, 300)]);

        // Assert
        Assert.Equal(1244, width);
    }

    [Fact]
    public void WhenCanvasHasNoNodesAndNoWidth_ThenTheDesignWidthIsZero()
    {
        // Act & Assert
        Assert.Equal(0, CanvasExtent.GetDesignWidth(null, []));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void WhenWidthIsNotAboveZero_ThenItIsAnError(int value)
    {
        // Act & Assert
        Assert.Equal($"Width must be above 0: {value}", CanvasExtent.Validate(value, null));
    }

    [Theory]
    [InlineData(CanvasScale.FitWidth, false, 1000, true)]
    [InlineData(CanvasScale.FitWidth, true, 1000, false)]
    [InlineData(CanvasScale.None, false, 1000, false)]
    [InlineData(CanvasScale.FitWidth, false, 0, false)]
    public void WhenCanvasIsChecked_ThenOnlyAViewedFitWidthCanvasWithAWidthIsScaled(
        CanvasScale scale, bool isEditing, int designWidth, bool expected)
    {
        // Act
        var isScaled = CanvasExtent.IsScaled(scale, isEditing, designWidth);

        // Assert
        Assert.Equal(expected, isScaled);
    }

    [Fact]
    public void WhenCanvasHasNoMinWidth_ThenItIsDrawnAtTheWidthOfItsFrame()
    {
        // Act
        var factor = CanvasExtent.GetFactor(1104, null);

        // Assert
        Assert.Equal("100cqw", CanvasExtent.GetDrawnWidth(null));
        Assert.Equal("tan(atan2(100cqw, 1104px))", factor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void WhenMinWidthIsNotAboveZero_ThenItIsAnError(int value)
    {
        // Act & Assert
        Assert.Equal($"MinWidth must be above 0: {value}", CanvasExtent.Validate(null, value));
    }

    [Fact]
    public void WhenCanvasHasAMinWidth_ThenItIsDrawnAtLeastThatWide()
    {
        // Act
        var factor = CanvasExtent.GetFactor(1000, 750);

        // Assert
        Assert.Equal("max(100cqw, 750px)", CanvasExtent.GetDrawnWidth(750));
        Assert.Equal("tan(atan2(max(100cqw, 750px), 1000px))", factor);
    }
}
