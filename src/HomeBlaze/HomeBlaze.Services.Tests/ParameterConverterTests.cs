namespace HomeBlaze.Services.Tests;

public class ParameterConverterTests
{
    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    [InlineData(typeof(double))]
    [InlineData(typeof(float))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(TimeSpan))]
    public void IsSupported_ReturnsTrueForPrimitiveTypes(Type type)
    {
        // Act
        var result = ParameterConverter.IsSupported(type);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(typeof(int?))]
    [InlineData(typeof(bool?))]
    [InlineData(typeof(DateTime?))]
    [InlineData(typeof(Guid?))]
    public void IsSupported_ReturnsTrueForNullablePrimitives(Type type)
    {
        // Act
        var result = ParameterConverter.IsSupported(type);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsSupported_ReturnsTrueForEnums()
    {
        // Act
        var result = ParameterConverter.IsSupported(typeof(DayOfWeek));

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsSupported_ReturnsTrueForNullableEnums()
    {
        // Act
        var result = ParameterConverter.IsSupported(typeof(DayOfWeek?));

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(typeof(object))]
    [InlineData(typeof(List<int>))]
    [InlineData(typeof(Dictionary<string, int>))]
    public void IsSupported_ReturnsFalseForComplexTypes(Type type)
    {
        // Act
        var result = ParameterConverter.IsSupported(type);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void TryConvert_String_ReturnsInput()
    {
        // Act
        var success = ParameterConverter.TryConvert("hello", typeof(string), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal("hello", value);
    }

    [Fact]
    public void TryConvert_EmptyStringToNullableString_ReturnsNull()
    {
        // Act
        var success = ParameterConverter.TryConvert("", typeof(string), out var value);

        // Assert
        Assert.True(success);
        Assert.Null(value);
    }

    [Theory]
    [InlineData("123", 123)]
    [InlineData("-456", -456)]
    [InlineData("0", 0)]
    public void TryConvert_Int_ParsesCorrectly(string input, int expected)
    {
        // Act
        var success = ParameterConverter.TryConvert(input, typeof(int), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryConvert_InvalidInt_ReturnsFalse()
    {
        // Act
        var success = ParameterConverter.TryConvert("abc", typeof(int), out var value);

        // Assert
        Assert.False(success);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    [InlineData("False", false)]
    public void TryConvert_Bool_ParsesCorrectly(string input, bool expected)
    {
        // Act
        var success = ParameterConverter.TryConvert(input, typeof(bool), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryConvert_Guid_ParsesCorrectly()
    {
        // Arrange
        var expected = Guid.NewGuid();

        // Act
        var success = ParameterConverter.TryConvert(expected.ToString(), typeof(Guid), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryConvert_TimeSpan_ParsesCorrectly()
    {
        // Act
        var success = ParameterConverter.TryConvert("01:30:00", typeof(TimeSpan), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(TimeSpan.FromMinutes(90), value);
    }

    [Fact]
    public void TryConvert_Enum_ParsesCorrectly()
    {
        // Act
        var success = ParameterConverter.TryConvert("Monday", typeof(DayOfWeek), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(DayOfWeek.Monday, value);
    }

    [Fact]
    public void TryConvert_Enum_IgnoresCase()
    {
        // Act
        var success = ParameterConverter.TryConvert("monday", typeof(DayOfWeek), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(DayOfWeek.Monday, value);
    }

    [Fact]
    public void TryConvert_NullableInt_ParsesCorrectly()
    {
        // Act
        var success = ParameterConverter.TryConvert("42", typeof(int?), out var value);

        // Assert
        Assert.True(success);
        Assert.Equal(42, value);
    }

    [Fact]
    public void TryConvert_EmptyToNullableInt_ReturnsNull()
    {
        // Act
        var success = ParameterConverter.TryConvert("", typeof(int?), out var value);

        // Assert
        Assert.True(success);
        Assert.Null(value);
    }

    [Fact]
    public void TryConvert_EmptyToRequiredInt_ReturnsFalse()
    {
        // Act
        var success = ParameterConverter.TryConvert("", typeof(int), out var value);

        // Assert
        Assert.False(success);
    }
}
