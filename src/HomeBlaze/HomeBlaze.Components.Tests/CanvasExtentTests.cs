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
        Assert.Equal($"Width must be above 0: {value}", CanvasExtent.Validate(value, null, null));
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
        var angle = CanvasExtent.GetAngle(1104, null, null);

        // Assert
        Assert.Equal("100cqw", CanvasExtent.GetDrawnWidth(null, null));
        Assert.Equal("atan2(100cqw, 1104px)", angle);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void WhenMinWidthIsNotAboveZero_ThenItIsAnError(int value)
    {
        // Act & Assert
        Assert.Equal($"MinWidth must be above 0: {value}", CanvasExtent.Validate(null, value, null));
    }

    [Fact]
    public void WhenCanvasHasAMinWidth_ThenItIsDrawnAtLeastThatWide()
    {
        // Act
        var angle = CanvasExtent.GetAngle(1000, 750, null);

        // Assert
        Assert.Equal("max(100cqw, 750px)", CanvasExtent.GetDrawnWidth(750, null));
        Assert.Equal("atan2(max(100cqw, 750px), 1000px)", angle);
    }

    [Fact]
    public void WhenCanvasHasAMaxWidth_ThenItIsDrawnAtMostThatWide()
    {
        // Act
        var angle = CanvasExtent.GetAngle(1104, null, 1104);

        // Assert
        Assert.Equal("min(100cqw, 1104px)", CanvasExtent.GetDrawnWidth(null, 1104));
        Assert.Equal("atan2(min(100cqw, 1104px), 1104px)", angle);
    }

    [Fact]
    public void WhenCanvasHasAMinWidthAndAMaxWidth_ThenItIsDrawnBetweenBoth()
    {
        // Act
        var drawnWidth = CanvasExtent.GetDrawnWidth(720, 1104);

        // Assert
        Assert.Equal("clamp(720px, 100cqw, 1104px)", drawnWidth);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void WhenMaxWidthIsNotAboveZero_ThenItIsAnError(int value)
    {
        // Act & Assert
        Assert.Equal($"MaxWidth must be above 0: {value}", CanvasExtent.Validate(null, null, value));
    }

    [Fact]
    public void WhenMaxWidthIsBelowMinWidth_ThenItIsAnError()
    {
        // Act & Assert
        Assert.Equal("MaxWidth must not be below MinWidth: 600", CanvasExtent.Validate(null, 720, 600));
        Assert.Null(CanvasExtent.Validate(null, 720, 720));
    }
}
