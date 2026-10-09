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
    [InlineData(typeof(TimeSpan), "[d.]hh:mm:ss[.fffffff]")]
    [InlineData(typeof(TimeSpan?), "[d.]hh:mm:ss[.fffffff]")]
    [InlineData(typeof(DateTime), "date-time")]
    [InlineData(typeof(DateTimeOffset), "date-time")]
    [InlineData(typeof(DateOnly), "date")]
    [InlineData(typeof(TimeOnly), "HH:mm:ss[.fffffff]")]
    [InlineData(typeof(Guid), "uuid")]
    [InlineData(typeof(Uri), "uri")]
    public void WhenTypeHasStringFormat_ThenReturnsFormat(Type clrType, string expected)
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
    [InlineData(null)]
    public void WhenTypeHasNoFormat_ThenReturnsNull(Type? clrType)
    {
        // Act
        var format = JsonSchemaTypeMapper.GetFormat(clrType);

        // Assert
        Assert.Null(format);
    }

    [Theory]
    [InlineData("00:01:30", 90)]
    [InlineData("1.00:00:30", 86430)]
    [InlineData("00:00:01.5", 1.5)]
    public void WhenTimeSpanArgumentMatchesFormat_ThenJsonSerializerAcceptsIt(string argument, double expectedSeconds)
    {
        // Act
        var value = JsonSerializer.Deserialize<TimeSpan>(JsonSerializer.Serialize(argument));

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), value);
    }
}
