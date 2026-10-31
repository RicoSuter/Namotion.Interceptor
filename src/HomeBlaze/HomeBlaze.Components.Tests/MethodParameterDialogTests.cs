using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Dialogs;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class MethodParameterDialogTests
{
    [Theory]
    [InlineData(8, "8")]
    [InlineData(1.5, "1.5")]
    [InlineData(true, "True")]
    [InlineData("text", "text")]
    [InlineData(DayOfWeek.Friday, "Friday")]
    public void WhenAnInitialValueIsFormatted_ThenTheFieldShowsItAsEntered(object value, string expected)
    {
        // Act
        var text = MethodParameterDialog.FormatInitialValue(value, unit: null);

        // Assert
        Assert.Equal(expected, text);
    }

    [Fact]
    public void WhenAPercentIsFormatted_ThenTheFieldShowsItFrom0To100()
    {
        // Act
        var fromDecimal = MethodParameterDialog.FormatInitialValue(0.5m, StateUnit.Percent);
        var fromDouble = MethodParameterDialog.FormatInitialValue(0.333, StateUnit.Percent);

        // Assert
        Assert.Equal("50", fromDecimal);
        Assert.Equal("33.3", fromDouble);
    }

    [Fact]
    public void WhenTheInitialValueIsNull_ThenTheFieldStaysEmpty()
    {
        // Act
        var text = MethodParameterDialog.FormatInitialValue(null, unit: null);

        // Assert
        Assert.Null(text);
    }
}
