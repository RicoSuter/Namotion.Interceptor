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
        Assert.Null(color.Style);
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
    [InlineData("var(--mud-palette-primary-darken)")]
    public void WhenTextIsACssColor_ThenItIsKeptAsWrittenAndRedefinesThePrimaryColor(string text)
    {
        // Act
        var isColor = ValueColor.TryParse(" " + text + " ", out var color);

        // Assert
        Assert.True(isColor);
        Assert.Equal(text, color!.Css);
        Assert.Equal(Color.Primary, color.Theme);
        Assert.False(color.IsDefault);
        Assert.StartsWith($"--mud-palette-primary: {text}; ", color.Style);
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
    [InlineData("rgb(100%, 0, 0)")]
    [InlineData("rgb(255,\u00a0255, 255)")]
    [InlineData("rgb(٣, 0, 0)")]
    [InlineData("hsl(0, 0%, 100%)")]
    [InlineData("var(--my-color)")]
    [InlineData("var(--mud-palette-primary, red)")]
    [InlineData("var(--mud-palette-primary);color:red")]
    [InlineData("var(--mud-palette-primary-hover)")]
    [InlineData("var(--mud-palette-primary-text)")]
    [InlineData("var(--mud-palette-success-rgb)")]
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
    public void WhenCssColorIsParsed_ThenTheTextOnItIsBlackOrWhiteWhicheverContrastsMore(string text, string expectedText)
    {
        // Act
        ValueColor.TryParse(text, out var color);

        // Assert
        Assert.Equal(
            $"--mud-palette-primary: {text}; --mud-palette-primary-text: {expectedText}; --mud-palette-primary-hover: color-mix(in srgb, {text} 6%, transparent)",
            color!.Style);
    }

    [Theory]
    [InlineData("var(--mud-palette-primary)", Color.Primary)]
    [InlineData("var(--mud-palette-success)", Color.Success)]
    [InlineData("var( --mud-palette-dark )", Color.Dark)]
    public void WhenCssColorIsThePaletteVariableOfAThemeColor_ThenItIsThatThemeColorWithoutStyle(string text, Color expected)
    {
        // Act
        var isColor = ValueColor.TryParse(text, out var color);

        // Assert
        Assert.True(isColor);
        Assert.Same(ValueColor.FromTheme(expected), color);
        Assert.Null(color!.Style);
    }

    [Fact]
    public void WhenCssColorIsAPaletteVariable_ThenTheTextOnItHasTheTextColorOfTheTheme()
    {
        // Act
        ValueColor.TryParse("var(--mud-palette-gray-light)", out var color);

        // Assert
        Assert.Contains("--mud-palette-primary-text: var(--mud-palette-text-primary);", color!.Style);
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
        var isInvalidValid = mapCase.IsColorValid();
        mapCase.Color = "Success";
        var theme = mapCase.GetColor();

        // Assert
        Assert.Same(first, second);
        Assert.Equal("#ffffff80", first!.Css);
        Assert.Same(ValueColor.Default, invalid);
        Assert.False(isInvalidValid);
        Assert.Same(ValueColor.FromTheme(Color.Success), theme);
    }

    [Fact]
    public void WhenThemeColorIsNotDefinedByTheEnum_ThenItIsTheDefaultColor()
    {
        // Act
        ValueColor color = (Color)99;

        // Assert
        Assert.Same(ValueColor.Default, color);
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
