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
        Assert.Equal($"Width must be above 0: {value}", CanvasExtent.Validate(value));
    }

    [Theory]
    [InlineData(CanvasScale.FitWidth, false, 1000, 500, 0.5)]
    [InlineData(CanvasScale.FitWidth, false, 1000, 1500, 1.5)]
    [InlineData(CanvasScale.FitWidth, true, 1000, 500, 1)]
    [InlineData(CanvasScale.None, false, 1000, 500, 1)]
    [InlineData(CanvasScale.FitWidth, false, 0, 500, 1)]
    [InlineData(CanvasScale.FitWidth, false, 1000, 0, 1)]
    public void WhenFactorIsComputed_ThenOnlyAViewedFitWidthCanvasWithAWidthIsScaled(
        CanvasScale scale, bool isEditing, int designWidth, double availableWidth, double expected)
    {
        // Act
        var factor = CanvasExtent.GetFactor(scale, isEditing, designWidth, availableWidth);

        // Assert
        Assert.Equal(expected, factor, 6);
    }
}
