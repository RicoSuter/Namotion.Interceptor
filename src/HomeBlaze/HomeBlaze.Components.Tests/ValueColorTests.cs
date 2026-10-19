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

    [Fact]
    public void WhenCaseTextColorIsReadTwice_ThenItIsParsedOnceUntilTheTextChanges()
    {
        // Arrange
        var mapCase = new ValueMapCase { Color = "Info", TextColor = "#ffffff" };

        // Act
        var first = mapCase.GetTextColor();
        var second = mapCase.GetTextColor();
        mapCase.TextColor = "warning";
        var theme = mapCase.GetTextColor();
        mapCase.TextColor = null;
        var none = mapCase.GetTextColor();

        // Assert
        Assert.Same(first, second);
        Assert.Equal("#ffffff", first.Css);
        Assert.Same(ValueColor.FromTheme(Color.Warning), theme);
        Assert.Same(ValueColor.Default, none);
        Assert.True(mapCase.IsTextColorValid());
        Assert.Same(ValueColor.FromTheme(Color.Info), mapCase.GetColor());
    }

    [Theory]
    [InlineData("white")]
    [InlineData("#fff; background: red")]
    [InlineData("#ffffff\"")]
    [InlineData("rgb(255, 255, 255); position: fixed")]
    [InlineData("var(--mud-palette-text-primary); background: url(x)")]
    [InlineData("expression(alert(1))")]
    [InlineData("4")]
    public void WhenCaseTextColorIsNoAllowedColor_ThenItIsInvalidAndHasNoTextColor(string text)
    {
        // Arrange
        var mapCase = new ValueMapCase { Color = "Info", TextColor = text };

        // Act
        var isValid = mapCase.IsTextColorValid();
        var textColor = mapCase.GetTextColor();

        // Assert
        Assert.False(isValid);
        Assert.Same(ValueColor.Default, textColor);
        Assert.True(mapCase.IsColorValid());
    }

    [Fact]
    public void WhenTextColorIsOnAThemeFill_ThenTheStyleOfAComponentHasTheTextColorAlone()
    {
        // Arrange
        ValueColor.TryParse("#ffffff", out var textColor);

        // Act
        var textOnFill = ValueColor.GetTextOnFill(Color.Info, textColor!);

        // Assert
        Assert.Equal("color: #ffffff", textOnFill.Style);
        Assert.Same(textOnFill, ValueColor.GetTextOnFill(Color.Info, textColor!));
    }

    [Fact]
    public void WhenTextColorIsOnACssFill_ThenTheStyleOfAComponentRedefinesThePrimaryColorAndHasTheTextColor()
    {
        // Arrange
        ValueColor.TryParse("#000080", out var fill);

        // Act
        var textOnFill = ValueColor.GetTextOnFill(fill!, Color.Warning);

        // Assert
        Assert.Equal(fill!.Style + "; color: var(--mud-palette-warning)", textOnFill.Style);
        Assert.Same(textOnFill, ValueColor.GetTextOnFill(fill, Color.Warning));
    }

    [Theory]
    [InlineData(Color.Surface, "var(--mud-palette-surface)")]
    [InlineData(Color.Inherit, "inherit")]
    [InlineData(Color.Dark, "var(--mud-palette-dark)")]
    public void WhenTextColorIsAThemeColor_ThenItsCssValueIsThatOfThePalette(Color text, string expected)
    {
        // Act
        var textOnFill = ValueColor.GetTextOnFill(Color.Primary, text);

        // Assert
        Assert.Equal(expected, textOnFill.TextCss);
    }

    [Theory]
    [InlineData("Surface 30%", "color-mix(in srgb, var(--mud-palette-surface) 30%, transparent)", "var(--mud-palette-text-primary)")]
    [InlineData(" warning  50% ", "color-mix(in srgb, var(--mud-palette-warning) 50%, transparent)", "var(--mud-palette-warning-text)")]
    [InlineData("Dark 12.5%", "color-mix(in srgb, var(--mud-palette-dark) 12.5%, transparent)", "var(--mud-palette-dark-text)")]
    [InlineData("Info 0%", "color-mix(in srgb, var(--mud-palette-info) 0%, transparent)", "var(--mud-palette-info-text)")]
    [InlineData("Error 100%", "color-mix(in srgb, var(--mud-palette-error) 100%, transparent)", "var(--mud-palette-error-text)")]
    [InlineData("Success 007%", "color-mix(in srgb, var(--mud-palette-success) 7%, transparent)", "var(--mud-palette-success-text)")]
    [InlineData("Primary 30%", "rgba(var(--mud-palette-primary-rgb), 0.3)", "var(--mud-palette-primary-text)")]
    public void WhenTextIsAThemeColorWithAPercentage_ThenItIsThatPaletteColorWithThatAlpha(string text, string expectedCss, string expectedText)
    {
        // Act
        var isColor = ValueColor.TryParse(text, out var color);

        // Assert
        Assert.True(isColor);
        Assert.Equal(Color.Primary, color!.Theme);
        Assert.Equal(expectedCss, color.Css);
        Assert.Equal(expectedText, color.ContrastText);
        Assert.StartsWith($"--mud-palette-primary: {expectedCss}; ", color.Style);
    }

    [Theory]
    [InlineData("Primary 30%")]
    [InlineData("Secondary 30%")]
    [InlineData("Tertiary 30%")]
    [InlineData("Info 30%")]
    [InlineData("Success 30%")]
    [InlineData("Warning 30%")]
    [InlineData("Error 30%")]
    [InlineData("Dark 30%")]
    [InlineData("Surface 30%")]
    public void WhenThemeColorWithAPercentageIsParsed_ThenNoVariableOfItsStyleRefersToItself(string text)
    {
        // Arrange
        ValueColor.TryParse(text, out var color);

        // Act
        var declarations = color!.Style!.Split("; ").Select(declaration => declaration.Split(": ", 2)).ToArray();

        // Assert: a custom property defined by itself on the same element is invalid, and so is what refers to it.
        Assert.All(declarations, declaration => Assert.DoesNotContain($"var({declaration[0]})", declaration[1]));
    }

    [Theory]
    [InlineData("Surface 101%")]
    [InlineData("Surface 1000%")]
    [InlineData("Surface 30")]
    [InlineData("Surface -30%")]
    [InlineData("Surface +30%")]
    [InlineData("Surface 30 %")]
    [InlineData("Surface30%")]
    [InlineData("Surface .5%")]
    [InlineData("Surface 5.%")]
    [InlineData("Surface 1e2%")]
    [InlineData("Surface 30%%")]
    [InlineData("Surface 30%; color: red")]
    [InlineData("Surface 30%)")]
    [InlineData("Surface\u00a030%")]
    [InlineData("Surface 3\u00b40%")]
    [InlineData("Surface 30% 40%")]
    [InlineData("Default 30%")]
    [InlineData("Inherit 30%")]
    [InlineData("Transparent 30%")]
    [InlineData("Purple 30%")]
    [InlineData("3 30%")]
    [InlineData("30% Surface")]
    [InlineData("var(--mud-palette-surface) 30%")]
    public void WhenThemeColorWithAPercentageIsMalformed_ThenItIsNoColor(string text)
    {
        // Act
        var isColor = ValueColor.TryParse(text, out var color);

        // Assert
        Assert.False(isColor);
        Assert.Null(color);
    }

    [Fact]
    public void WhenTextColorIsAThemeColorWithAPercentage_ThenTheTextHasTheMixedColor()
    {
        // Arrange
        ValueColor.TryParse("Surface 70%", out var textColor);

        // Act
        var textOnFill = ValueColor.GetTextOnFill(Color.Info, textColor!);

        // Assert
        Assert.Equal("color-mix(in srgb, var(--mud-palette-surface) 70%, transparent)", textOnFill.TextCss);
        Assert.Equal("color: color-mix(in srgb, var(--mud-palette-surface) 70%, transparent)", textOnFill.Style);
    }
}
