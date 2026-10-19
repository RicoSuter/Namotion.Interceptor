using MudBlazor;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ValueColorTests
{
    private const string DarkText = "rgba(0, 0, 0, 0.87)";
    private const string LightText = "#ffffff";

    [Theory]
    [InlineData("Success", Color.Success)]
    [InlineData("success", Color.Success)]
    [InlineData(" Warning ", Color.Warning)]
    [InlineData("Default", Color.Default)]
    public void WhenTextIsTheNameOfAThemeColor_ThenItIsThatThemeColorWithoutStyle(string text, Color expected)
    {
        // Act
        var isColor = ValueColor.TryParse(text, out var color);

        // Assert
        Assert.True(isColor);
        Assert.Same(ValueColor.FromTheme(expected), color);
        Assert.Equal(expected, color!.Theme);
        Assert.Null(color.Css);
        Assert.Null(color.TextStyle);
        Assert.Null(color.FillStyle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenTextIsMissing_ThenItIsTheDefaultColor(string? text)
    {
        // Act
        var isColor = ValueColor.TryParse(text, out var color);

        // Assert
        Assert.True(isColor);
        Assert.Same(ValueColor.Default, color);
        Assert.True(color!.IsDefault);
    }

    [Theory]
    [InlineData("#fff")]
    [InlineData("#fff8")]
    [InlineData("#ffffff")]
    [InlineData("#FFFFFF80")]
    [InlineData("rgb(255, 255, 255)")]
    [InlineData("rgba(255,255,255,0.5)")]
    [InlineData("rgba(100%, 100%, 100%, .5)")]
    [InlineData("rgb(255 255 255)")]
    [InlineData("rgb(255 255 255 / 50%)")]
    [InlineData("var(--mud-palette-text-secondary)")]
    public void WhenTextIsACssColor_ThenItIsKeptAsWrittenAndIsNoThemeColor(string text)
    {
        // Act
        var isColor = ValueColor.TryParse(" " + text + " ", out var color);

        // Assert
        Assert.True(isColor);
        Assert.Equal(text, color!.Css);
        Assert.Equal(Color.Default, color.Theme);
        Assert.False(color.IsDefault);
        Assert.Equal($"color: {text}", color.TextStyle);
    }

    [Theory]
    [InlineData("Purple")]
    [InlineData("3")]
    [InlineData("Primary, Success")]
    [InlineData("white")]
    [InlineData("#ff")]
    [InlineData("#fffff")]
    [InlineData("#ggg")]
    [InlineData("#fff;color:red")]
    [InlineData("rgb(255, 255)")]
    [InlineData("rgb(255, 255 255)")]
    [InlineData("rgb(255,255,255);background:url(x)")]
    [InlineData("rgb(٣, 0, 0)")]
    [InlineData("hsl(0, 0%, 100%)")]
    [InlineData("var(--my-color)")]
    [InlineData("var(--mud-palette-primary, red)")]
    [InlineData("var(--mud-palette-primary);color:red")]
    [InlineData("url(x)")]
    [InlineData("expression(alert(1))")]
    public void WhenTextIsNoThemeColorAndNoAllowedCssColor_ThenItIsNoColor(string text)
    {
        // Act
        var isColor = ValueColor.TryParse(text, out var color);

        // Assert
        Assert.False(isColor);
        Assert.Null(color);
    }

    [Theory]
    [InlineData("#ffffff80", DarkText)]
    [InlineData("#ff0", DarkText)]
    [InlineData("rgb(255 255 255 / 50%)", DarkText)]
    [InlineData("#000080", LightText)]
    [InlineData("rgba(0, 0, 0, 0.5)", LightText)]
    [InlineData("rgb(0%, 0%, 50%)", LightText)]
    public void WhenCssColorFillsAChip_ThenTheTextIsBlackOrWhiteWhicheverContrastsMore(string text, string expectedText)
    {
        // Act
        ValueColor.TryParse(text, out var color);

        // Assert
        Assert.Equal($"background-color: {text}; color: {expectedText}", color!.FillStyle);
    }

    [Fact]
    public void WhenCssColorIsAPaletteVariable_ThenAFillHasTheTextColorOfTheTheme()
    {
        // Act
        ValueColor.TryParse("var(--mud-palette-gray-light)", out var color);

        // Assert
        Assert.Equal("background-color: var(--mud-palette-gray-light); color: var(--mud-palette-text-primary)", color!.FillStyle);
    }

    [Fact]
    public void WhenThemeColorIsConverted_ThenEveryConversionGivesTheSameInstance()
    {
        // Act
        ValueColor first = Color.Error;
        ValueColor second = Color.Error;

        // Assert
        Assert.Same(first, second);
        Assert.Equal(Color.Error, first.Theme);
    }

    [Fact]
    public void WhenCaseColorIsReadTwice_ThenItIsParsedOnceUntilTheTextChanges()
    {
        // Arrange
        var mapCase = new ValueMapCase { Color = "#ffffff80" };

        // Act
        var first = mapCase.GetColor();
        var second = mapCase.GetColor();
        mapCase.Color = "Purple";
        var invalid = mapCase.GetColor();
        mapCase.Color = "Success";
        var theme = mapCase.GetColor();

        // Assert
        Assert.Same(first, second);
        Assert.Equal("#ffffff80", first!.Css);
        Assert.Null(invalid);
        Assert.Same(ValueColor.FromTheme(Color.Success), theme);
    }

    [Fact]
    public void WhenColorIsNoColor_ThenTheErrorShowsItAsWritten()
    {
        // Act
        var error = ValueColor.GetError("Purple");

        // Assert
        Assert.Equal("Unknown color 'Purple': use a theme color like Success or a CSS color like #ffffff80", error);
    }
}
