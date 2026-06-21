using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Abstractions.Tests;

public class HistoryColumnsTests
{
    private enum SampleEnum
    {
        A,
        B
    }

    [Theory]
    [InlineData(typeof(double))]
    [InlineData(typeof(float))]
    [InlineData(typeof(double?))]
    [InlineData(typeof(float?))]
    public void WhenTypeIsFloatingPoint_ThenColumnIsDouble(Type type)
    {
        // Arrange & Act
        var column = HistoryColumns.ValueColumnFor(type);

        // Assert
        Assert.Equal(ValueColumn.Double, column);
    }

    [Theory]
    [InlineData(typeof(long))]
    [InlineData(typeof(int))]
    [InlineData(typeof(short))]
    [InlineData(typeof(sbyte))]
    [InlineData(typeof(byte))]
    [InlineData(typeof(ushort))]
    [InlineData(typeof(uint))]
    [InlineData(typeof(ulong))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(long?))]
    [InlineData(typeof(int?))]
    [InlineData(typeof(ulong?))]
    [InlineData(typeof(bool?))]
    public void WhenTypeIsIntegerOrBool_ThenColumnIsLong(Type type)
    {
        // Arrange & Act
        var column = HistoryColumns.ValueColumnFor(type);

        // Assert
        Assert.Equal(ValueColumn.Long, column);
    }

    [Theory]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(string))]
    [InlineData(typeof(SampleEnum))]
    [InlineData(typeof(decimal?))]
    [InlineData(typeof(SampleEnum?))]
    public void WhenTypeIsDecimalStringOrEnum_ThenColumnIsJson(Type type)
    {
        // Arrange & Act
        var column = HistoryColumns.ValueColumnFor(type);

        // Assert
        Assert.Equal(ValueColumn.Json, column);
    }

    [Theory]
    [InlineData(typeof(ulong))]
    [InlineData(typeof(ulong?))]
    public void WhenTypeIsUlong_ThenIsUlongPropertyIsTrue(Type type)
    {
        // Arrange & Act
        var result = HistoryColumns.IsUlongProperty(type);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(typeof(long))]
    [InlineData(typeof(uint))]
    [InlineData(typeof(double))]
    [InlineData(typeof(int))]
    [InlineData(typeof(string))]
    public void WhenTypeIsNotUlong_ThenIsUlongPropertyIsFalse(Type type)
    {
        // Arrange & Act
        var result = HistoryColumns.IsUlongProperty(type);

        // Assert
        Assert.False(result);
    }
}
