using HomeBlaze.Components.Controls;
using MudBlazor;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class TileStyleTests
{
    [Fact]
    public void WhenColorIsWarningAndFilled_ThenTheTileIsFilledWithItAndUsesItsTextColor()
    {
        // Act
        var style = TileStyle.GetStyle(Color.Warning, Variant.Filled, disabled: false);

        // Assert
        Assert.Equal("background: var(--mud-palette-warning); color: var(--mud-palette-warning-text)", style);
    }

    [Theory]
    [InlineData(Color.Default)]
    [InlineData(Color.Inherit)]
    [InlineData(Color.Transparent)]
    [InlineData(Color.Surface)]
    public void WhenColorHasNoPaletteFill_ThenTheTileIsDrawnOnTheSurface(Color color)
    {
        // Act
        var style = TileStyle.GetStyle(color, Variant.Filled, disabled: false);

        // Assert
        Assert.Equal("background: var(--mud-palette-surface); color: var(--mud-palette-text-primary)", style);
    }

    [Fact]
    public void WhenVariantIsOutlined_ThenTheTileHasABorderInTheColor()
    {
        // Act
        var style = TileStyle.GetStyle(Color.Primary, Variant.Outlined, disabled: false);

        // Assert
        Assert.Equal("background: var(--mud-palette-surface); color: var(--mud-palette-primary); box-shadow: inset 0 0 0 2px var(--mud-palette-primary)", style);
    }

    [Fact]
    public void WhenVariantIsText_ThenOnlyTheTextHasTheColor()
    {
        // Act
        var style = TileStyle.GetStyle(Color.Error, Variant.Text, disabled: false);

        // Assert
        Assert.Equal("background: var(--mud-palette-surface); color: var(--mud-palette-error)", style);
    }

    [Fact]
    public void WhenTileIsDisabled_ThenItHasTheDisabledColorsWhateverItsColor()
    {
        // Act
        var style = TileStyle.GetStyle(Color.Warning, Variant.Filled, disabled: true);

        // Assert
        Assert.Equal("background: var(--mud-palette-action-disabled-background); color: var(--mud-palette-action-disabled)", style);
    }

    [Fact]
    public void WhenStyleIsAskedTwice_ThenTheSameInstanceIsReturned()
    {
        // Act
        var first = TileStyle.GetStyle(Color.Success, Variant.Outlined, disabled: false);
        var second = TileStyle.GetStyle(Color.Success, Variant.Outlined, disabled: false);

        // Assert
        Assert.Same(first, second);
    }
}
