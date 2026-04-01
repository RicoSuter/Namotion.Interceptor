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
}
