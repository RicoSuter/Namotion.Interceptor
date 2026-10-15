using Xunit;

namespace HomeBlaze.Components.Tests;

public class CardSizeTests
{
    [Theory]
    [InlineData("4:3", 4, 3)]
    [InlineData("1:1", 1, 1)]
    [InlineData(" 16 : 9 ", 16, 9)]
    [InlineData("1.85:1", 1.85, 1)]
    public void WhenAspectRatioIsTwoPositiveNumbersWithAColon_ThenItIsParsed(string value, double expectedWidth, double expectedHeight)
    {
        // Act
        var parsed = CardSize.TryParseAspectRatio(value, out var width, out var height);

        // Assert
        Assert.True(parsed);
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Theory]
    [InlineData("4/3")]
    [InlineData("0:1")]
    [InlineData("1:0")]
    [InlineData("-4:3")]
    [InlineData("a:b")]
    [InlineData("4:3:2")]
    [InlineData("4:")]
    [InlineData("")]
    [InlineData("NaN:1")]
    [InlineData("Infinity:1")]
    public void WhenAspectRatioIsNotTwoPositiveNumbersWithAColon_ThenItIsNotParsed(string value)
    {
        // Act
        var parsed = CardSize.TryParseAspectRatio(value, out _, out _);

        // Assert
        Assert.False(parsed);
    }

    [Fact]
    public void WhenNeitherHeightNorAspectRatioIsSet_ThenTheCardHasNoSizeStyle()
    {
        // Act
        var style = CardSize.GetStyle(null, null, out var error);

        // Assert
        Assert.Null(style);
        Assert.Null(error);
    }

    [Fact]
    public void WhenHeightIsSet_ThenTheCardHasThatHeightInPixels()
    {
        // Act
        var style = CardSize.GetStyle(150, null, out var error);

        // Assert
        Assert.Equal("height: 150px", style);
        Assert.Null(error);
    }

    [Fact]
    public void WhenAspectRatioIsSet_ThenTheCardHasThatAspectRatio()
    {
        // Act
        var style = CardSize.GetStyle(null, " 1.5 : 1 ", out var error);

        // Assert
        Assert.Equal("aspect-ratio: 1.5 / 1", style);
        Assert.Null(error);
    }

    [Fact]
    public void WhenHeightAndAspectRatioAreSet_ThenHeightWins()
    {
        // Act
        var style = CardSize.GetStyle(150, "4:3", out var error);

        // Assert
        Assert.Equal("height: 150px", style);
        Assert.Null(error);
    }

    [Fact]
    public void WhenAspectRatioIsInvalid_ThenItIsAnErrorThatNamesTheValue()
    {
        // Act
        var style = CardSize.GetStyle(null, "4/3", out var error);

        // Assert
        Assert.Null(style);
        Assert.Equal("AspectRatio must look like 4:3: 4/3", error);
    }

    [Fact]
    public void WhenAspectRatioIsInvalidAndHeightIsSet_ThenItIsStillAnError()
    {
        // Act
        var style = CardSize.GetStyle(150, "wide", out var error);

        // Assert
        Assert.Null(style);
        Assert.Equal("AspectRatio must look like 4:3: wide", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void WhenHeightIsNotAboveZero_ThenItIsAnErrorThatNamesTheValue(int height)
    {
        // Act
        var style = CardSize.GetStyle(height, null, out var error);

        // Assert
        Assert.Null(style);
        Assert.Equal($"Height must be above 0: {height}", error);
    }
}
