using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Services;
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

    public static TheoryData<object?, Type, StateUnit?> RoundTripValues => new()
    {
        { new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"), typeof(Guid), null },
        { new TimeSpan(1, 2, 3, 4, 567), typeof(TimeSpan), null },
        { new DateTime(2026, 10, 31, 12, 34, 56, DateTimeKind.Unspecified).AddTicks(1234567), typeof(DateTime), null },
        { new DateTime(2026, 10, 31, 12, 34, 56, DateTimeKind.Local).AddTicks(1234567), typeof(DateTime), null },
        { new DateTimeOffset(2026, 10, 31, 12, 34, 56, TimeSpan.FromHours(2)).AddTicks(1234567), typeof(DateTimeOffset), null },
        { (int?)42, typeof(int?), null },
        { null, typeof(int?), null },
        { 50, typeof(int), StateUnit.Percent }
    };

    [Theory]
    [MemberData(nameof(RoundTripValues))]
    public void WhenAnInitialValueIsFormatted_ThenTheFieldConvertsItBackToTheSameValue(object? value, Type type, StateUnit? unit)
    {
        // Act
        var text = MethodParameterDialog.FormatInitialValue(value, unit);
        var isConverted = ParameterConverter.TryConvert(text, type, unit, out var convertedValue);

        // Assert
        Assert.True(isConverted);
        Assert.Equal(value, convertedValue);
    }
}
