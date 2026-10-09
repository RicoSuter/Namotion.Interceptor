using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class JsonSchemaTypeMapperTests
{
    [Theory]
    [InlineData(typeof(string), "string")]
    [InlineData(typeof(bool), "boolean")]
    [InlineData(typeof(bool?), "boolean")]
    [InlineData(typeof(int), "integer")]
    [InlineData(typeof(long), "integer")]
    [InlineData(typeof(short), "integer")]
    [InlineData(typeof(byte), "integer")]
    [InlineData(typeof(double), "number")]
    [InlineData(typeof(float), "number")]
    [InlineData(typeof(decimal), "number")]
    [InlineData(typeof(int[]), "array")]
    [InlineData(typeof(List<string>), "array")]
    [InlineData(typeof(DateTime), "string")]
    [InlineData(typeof(DateTimeOffset), "string")]
    [InlineData(typeof(Guid), "string")]
    [InlineData(typeof(TimeSpan), "string")]
    [InlineData(typeof(TimeSpan?), "string")]
    [InlineData(typeof(DateOnly), "string")]
    [InlineData(typeof(TimeOnly), "string")]
    [InlineData(typeof(Uri), "string")]
    [InlineData(typeof(char), "string")]
    [InlineData(typeof(object), "object")]
    public void WhenMappingClrType_ThenReturnsCorrectJsonSchemaType(Type clrType, string expected)
    {
        Assert.Equal(expected, JsonSchemaTypeMapper.ToJsonSchemaType(clrType));
    }

    [Fact]
    public void WhenEnumType_ThenReturnsString()
    {
        Assert.Equal("string", JsonSchemaTypeMapper.ToJsonSchemaType(typeof(DayOfWeek)));
    }

    [Fact]
    public void WhenNullType_ThenReturnsNull()
    {
        Assert.Null(JsonSchemaTypeMapper.ToJsonSchemaType(null));
    }

    [Fact]
    public void WhenUnknownClassType_ThenReturnsObject()
    {
        Assert.Equal("object", JsonSchemaTypeMapper.ToJsonSchemaType(typeof(JsonSchemaTypeMapperTests)));
    }

    [Theory]
    [InlineData(typeof(DateTime), "date-time")]
    [InlineData(typeof(DateTimeOffset), "date-time")]
    [InlineData(typeof(DateTimeOffset?), "date-time")]
    [InlineData(typeof(DateOnly), "date")]
    [InlineData(typeof(Guid), "uuid")]
    [InlineData(typeof(Uri), "uri")]
    public void WhenTypeHasStandardFormat_ThenReturnsFormatName(Type clrType, string expected)
    {
        // Act
        var format = JsonSchemaTypeMapper.GetFormat(clrType);

        // Assert
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(int))]
    [InlineData(typeof(DayOfWeek))]
    [InlineData(typeof(TimeSpan))]
    [InlineData(typeof(TimeOnly))]
    [InlineData(null)]
    public void WhenTypeHasNoStandardFormat_ThenReturnsNull(Type? clrType)
    {
        // Act
        var format = JsonSchemaTypeMapper.GetFormat(clrType);

        // Assert
        Assert.Null(format);
    }

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(Guid))]
    [InlineData(null)]
    public void WhenTypeHasNoPattern_ThenReturnsNull(Type? clrType)
    {
        // Act
        var pattern = JsonSchemaTypeMapper.GetPattern(clrType);

        // Assert
        Assert.Null(pattern);
    }

    [Theory]
    [InlineData("00:01:30", 90)]
    [InlineData("1.00:00:30", 86430)]
    [InlineData("00:00:01.5", 1.5)]
    [InlineData("-00:00:10", -10)]
    public void WhenTimeSpanArgumentMatchesPattern_ThenJsonSerializerAcceptsIt(string argument, double expectedSeconds)
    {
        // Arrange
        var pattern = JsonSchemaTypeMapper.GetPattern(typeof(TimeSpan?))!;

        // Act
        var value = JsonSerializer.Deserialize<TimeSpan>(JsonSerializer.Serialize(argument));

        // Assert
        Assert.Matches(pattern, argument);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), value);
    }

    [Theory]
    [InlineData("07:30:00", 7, 30, 0)]
    [InlineData("23:59:59.25", 23, 59, 59)]
    public void WhenTimeOnlyArgumentMatchesPattern_ThenJsonSerializerAcceptsIt(string argument, int hour, int minute, int second)
    {
        // Arrange
        var pattern = JsonSchemaTypeMapper.GetPattern(typeof(TimeOnly))!;

        // Act
        var value = JsonSerializer.Deserialize<TimeOnly>(JsonSerializer.Serialize(argument));

        // Assert
        Assert.Matches(pattern, argument);
        Assert.Equal((hour, minute, second), (value.Hour, value.Minute, value.Second));
    }

    [Theory]
    [InlineData("1:30")]
    [InlineData("90 seconds")]
    [InlineData("PT1M30S")]
    public void WhenTimeSpanArgumentDoesNotMatchPattern_ThenPatternRejectsIt(string argument)
    {
        // Arrange
        var pattern = JsonSchemaTypeMapper.GetPattern(typeof(TimeSpan))!;

        // Act & Assert
        Assert.DoesNotMatch(pattern, argument);
    }
}
