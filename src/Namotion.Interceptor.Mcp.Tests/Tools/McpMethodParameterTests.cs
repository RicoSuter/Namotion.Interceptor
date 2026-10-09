using System.Text.Json;
using Namotion.Interceptor.Mcp.Models;
using Namotion.Interceptor.Mcp.Tools;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class McpMethodParameterTests
{
    [Fact]
    public void WhenParameterIsTimeSpan_ThenTypeIsStringWithPattern()
    {
        // Act
        var parameter = McpMethodParameter.Create("position", typeof(TimeSpan));

        // Assert
        Assert.Equal("position", parameter.Name);
        Assert.Equal("string", parameter.Type);
        Assert.Null(parameter.Format);
        Assert.Equal(JsonSchemaTypeMapper.GetPattern(typeof(TimeSpan)), parameter.Pattern);
        Assert.Null(parameter.EnumValues);
        Assert.False(parameter.IsNullable);
    }

    [Fact]
    public void WhenParameterIsEnum_ThenListsAllowedNames()
    {
        // Act
        var parameter = McpMethodParameter.Create("day", typeof(DayOfWeek));

        // Assert
        Assert.Equal("string", parameter.Type);
        Assert.Equal(Enum.GetNames<DayOfWeek>(), parameter.EnumValues);
    }

    [Fact]
    public void WhenParameterIsNullableValueType_ThenIsNullable()
    {
        // Act
        var parameter = McpMethodParameter.Create("count", typeof(int?));

        // Assert
        Assert.Equal("integer", parameter.Type);
        Assert.True(parameter.IsNullable);
    }

    [Fact]
    public void WhenParameterHasDescription_ThenSerializesIt()
    {
        // Arrange
        var parameter = McpMethodParameter.Create("title", typeof(string), isNullable: true, description: "Shown as the stream name.");

        // Act
        var json = JsonSerializer.Serialize(parameter);

        // Assert
        Assert.Equal("""{"name":"title","type":"string","nullable":true,"description":"Shown as the stream name."}""", json);
    }

    [Fact]
    public void WhenParameterHasNoHints_ThenSerializesOnlyNameAndType()
    {
        // Arrange
        var parameter = McpMethodParameter.Create("count", typeof(int));

        // Act
        var json = JsonSerializer.Serialize(parameter);

        // Assert
        Assert.Equal("""{"name":"count","type":"integer"}""", json);
    }

    [Fact]
    public void WhenParameterHasFormatPatternAndEnum_ThenSerializesThem()
    {
        // Act
        var dateJson = JsonSerializer.Serialize(McpMethodParameter.Create("start", typeof(DateTimeOffset)));
        var timeSpanJson = JsonSerializer.Serialize(McpMethodParameter.Create("position", typeof(TimeSpan)));
        var enumJson = JsonSerializer.Serialize(McpMethodParameter.Create("mode", typeof(TestMode)));

        // Assert
        Assert.Equal("""{"name":"start","type":"string","format":"date-time"}""", dateJson);
        var timeSpan = JsonDocument.Parse(timeSpanJson).RootElement;
        Assert.Equal(["name", "type", "pattern"], timeSpan.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonSchemaTypeMapper.GetPattern(typeof(TimeSpan)), timeSpan.GetProperty("pattern").GetString());
        Assert.Equal("""{"name":"mode","type":"string","enum":["Off","On"]}""", enumJson);
    }

    public enum TestMode
    {
        Off,
        On
    }
}
