using System.Text.Json;
using Namotion.Interceptor.Mcp.Models;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class McpMethodParameterTests
{
    [Fact]
    public void WhenParameterIsTimeSpan_ThenTypeIsStringWithFormat()
    {
        // Act
        var parameter = McpMethodParameter.Create("position", typeof(TimeSpan));

        // Assert
        Assert.Equal("position", parameter.Name);
        Assert.Equal("string", parameter.Type);
        Assert.Equal("[d.]hh:mm:ss[.fffffff]", parameter.Format);
        Assert.Null(parameter.Enum);
        Assert.False(parameter.IsNullable);
    }

    [Fact]
    public void WhenParameterIsEnum_ThenListsAllowedNames()
    {
        // Act
        var parameter = McpMethodParameter.Create("day", typeof(DayOfWeek));

        // Assert
        Assert.Equal("string", parameter.Type);
        Assert.Equal(Enum.GetNames<DayOfWeek>(), parameter.Enum);
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
    public void WhenParameterHasFormatAndEnum_ThenSerializesThem()
    {
        // Act
        var timeSpanJson = JsonSerializer.Serialize(McpMethodParameter.Create("position", typeof(TimeSpan)));
        var enumJson = JsonSerializer.Serialize(McpMethodParameter.Create("mode", typeof(TestMode)));

        // Assert
        Assert.Equal("""{"name":"position","type":"string","format":"[d.]hh:mm:ss[.fffffff]"}""", timeSpanJson);
        Assert.Equal("""{"name":"mode","type":"string","enum":["Off","On"]}""", enumJson);
    }

    public enum TestMode
    {
        Off,
        On
    }
}
