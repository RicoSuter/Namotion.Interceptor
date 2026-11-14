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
        var style = TileStyle.GetStyle(Color.Warning, Variant.Filled);

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
        var style = TileStyle.GetStyle(color, Variant.Filled);

        // Assert
        Assert.Equal("background: var(--mud-palette-surface); color: var(--mud-palette-text-primary)", style);
    }

    [Theory]
    [InlineData(99, (int)Variant.Filled)]
    [InlineData((int)Color.Primary, 99)]
    [InlineData(-1, (int)Variant.Filled)]
    [InlineData((int)Color.Primary, -1)]
    public void WhenColorOrVariantIsUndefined_ThenTheTileIsDrawnOnTheSurface(int color, int variant)
    {
        // Act
        var style = TileStyle.GetStyle((Color)color, (Variant)variant);

        // Assert
        Assert.Equal("background: var(--mud-palette-surface); color: var(--mud-palette-text-primary)", style);
    }

    [Fact]
    public void WhenVariantIsOutlined_ThenTheTileHasABorderInTheColor()
    {
        // Act
        var style = TileStyle.GetStyle(Color.Primary, Variant.Outlined);

        // Assert
        Assert.Equal("background: var(--mud-palette-surface); color: var(--mud-palette-primary); box-shadow: inset 0 0 0 2px var(--mud-palette-primary)", style);
    }

    [Fact]
    public void WhenVariantIsText_ThenOnlyTheTextHasTheColor()
    {
        // Act
        var style = TileStyle.GetStyle(Color.Error, Variant.Text);

        // Assert
        Assert.Equal("background: var(--mud-palette-surface); color: var(--mud-palette-error)", style);
    }

    [Fact]
    public void WhenStyleIsAskedTwice_ThenTheSameInstanceIsReturned()
    {
        // Act
        var first = TileStyle.GetStyle(Color.Success, Variant.Outlined);
        var second = TileStyle.GetStyle(Color.Success, Variant.Outlined);

        // Assert
        Assert.Same(first, second);
    }

    [Theory]
    [InlineData(Variant.Filled, "background: #ffffff80; color: rgba(0, 0, 0, 0.87)")]
    [InlineData(Variant.Outlined, "background: var(--mud-palette-surface); color: #ffffff80; box-shadow: inset 0 0 0 2px #ffffff80")]
    [InlineData(Variant.Text, "background: var(--mud-palette-surface); color: #ffffff80")]
    public void WhenColorIsACssColor_ThenTheTileUsesItLikeAThemeColor(Variant variant, string expected)
    {
        // Arrange
        ValueColor.TryParse("#ffffff80", out var color);

        // Act
        var style = TileStyle.GetStyle(color!, variant);

        // Assert
        Assert.Equal(expected, style);
        Assert.Same(style, TileStyle.GetStyle(color!, variant));
    }

    [Theory]
    [InlineData(Variant.Filled, "background: var(--mud-palette-info); color: #ffffff")]
    [InlineData(Variant.Outlined, "background: var(--mud-palette-surface); color: #ffffff; box-shadow: inset 0 0 0 2px var(--mud-palette-info)")]
    [InlineData(Variant.Text, "background: var(--mud-palette-surface); color: #ffffff")]
    public void WhenTextColorIsACssColor_ThenItReplacesTheTextColorOfTheFill(Variant variant, string expected)
    {
        // Arrange
        ValueColor.TryParse("#ffffff", out var textColor);

        // Act
        var style = TileStyle.GetStyle(Color.Info, variant, textColor);

        // Assert
        Assert.Equal(expected, style);
        Assert.Same(style, TileStyle.GetStyle(Color.Info, variant, textColor));
    }

    [Fact]
    public void WhenTextColorIsAThemeColor_ThenTheTextHasItsPaletteColorOnTheFill()
    {
        // Act
        var style = TileStyle.GetStyle(Color.Warning, Variant.Filled, Color.Dark);

        // Assert
        Assert.Equal("background: var(--mud-palette-warning); color: var(--mud-palette-dark)", style);
        Assert.Same(style, TileStyle.GetStyle(Color.Warning, Variant.Filled, Color.Dark));
    }

    [Fact]
    public void WhenFillIsACssColorAndTextColorIsSet_ThenTheTileHasBoth()
    {
        // Arrange
        ValueColor.TryParse("#37474f", out var fill);
        ValueColor.TryParse("rgb(255 255 255 / 50%)", out var textColor);

        // Act
        var style = TileStyle.GetStyle(fill!, Variant.Filled, textColor);

        // Assert
        Assert.Equal("background: #37474f; color: rgb(255 255 255 / 50%)", style);
    }

    [Fact]
    public void WhenColorHasNoFillAndTextColorIsSet_ThenTheTileIsOnTheSurfaceWithTheTextColor()
    {
        // Arrange
        ValueColor.TryParse("var(--mud-palette-text-secondary)", out var textColor);

        // Act
        var style = TileStyle.GetStyle(Color.Default, Variant.Filled, textColor);

        // Assert
        Assert.Equal("background: var(--mud-palette-surface); color: var(--mud-palette-text-secondary)", style);
    }

    [Fact]
    public void WhenTextColorIsDefault_ThenTheTileKeepsTheTextColorOfTheFill()
    {
        // Act
        var style = TileStyle.GetStyle(Color.Info, Variant.Filled, ValueColor.Default);

        // Assert
        Assert.Same(TileStyle.GetStyle(Color.Info, Variant.Filled), style);
        Assert.Equal("background: var(--mud-palette-info); color: var(--mud-palette-info-text)", style);
    }

    [Fact]
    public void WhenACssTextColorIsDrawnOnAnotherFill_ThenTheStyleFollowsTheFill()
    {
        // Arrange
        ValueColor.TryParse("#ffffff", out var textColor);
        TileStyle.GetStyle(Color.Info, Variant.Filled, textColor);

        // Act
        var style = TileStyle.GetStyle(Color.Error, Variant.Filled, textColor);

        // Assert
        Assert.Equal("background: var(--mud-palette-error); color: #ffffff", style);
    }
}
