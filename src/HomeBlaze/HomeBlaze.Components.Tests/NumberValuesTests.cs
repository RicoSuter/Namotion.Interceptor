using Xunit;

namespace HomeBlaze.Components.Tests;

public class NumberValuesTests
{
    public static TheoryData<object, double> Numbers => new()
    {
        { (byte)1, 1 },
        { (sbyte)-1, -1 },
        { (short)-2, -2 },
        { (ushort)2, 2 },
        { -3, -3 },
        { 3u, 3 },
        { -4L, -4 },
        { 4UL, 4 },
        { 0.5f, 0.5 },
        { 0.25, 0.25 },
        { 1.5m, 1.5 },
        { double.PositiveInfinity, double.PositiveInfinity },
    };

    [Theory]
    [MemberData(nameof(Numbers))]
    public void WhenValueHasANumericType_ThenItIsANumber(object value, double expected)
    {
        // Act
        var isNumber = NumberValues.TryGetNumber(value, out var number);

        // Assert
        Assert.True(isNumber);
        Assert.Equal(expected, number);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("12")]
    [InlineData(true)]
    [InlineData(double.NaN)]
    [InlineData(float.NaN)]
    public void WhenValueIsNullNoNumberOrNaN_ThenItIsNoNumber(object? value)
    {
        // Act
        var isNumber = NumberValues.TryGetNumber(value, out _);

        // Assert
        Assert.False(isNumber);
    }

    [Theory]
    [InlineData(0.8, "0.8")]
    [InlineData(3000.0, "3000")]
    [InlineData(double.NaN, "NaN")]
    public void WhenNumberIsFormatted_ThenItIsInvariant(double value, string expected)
    {
        // Act
        var text = NumberValues.Format(value);

        // Assert
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData(typeof(int), true)]
    [InlineData(typeof(int?), true)]
    [InlineData(typeof(decimal), true)]
    [InlineData(typeof(double), true)]
    [InlineData(typeof(char), false)]
    [InlineData(typeof(bool), false)]
    [InlineData(typeof(DayOfWeek), false)]
    [InlineData(typeof(Half), false)]
    [InlineData(typeof(string), false)]
    public void WhenATypeIsChecked_ThenItIsANumberTypeWhenItsValuesAreRead(Type type, bool expected)
    {
        // Act
        var isNumberType = NumberValues.IsNumberType(type);

        // Assert
        Assert.Equal(expected, isNumberType);
    }
}
