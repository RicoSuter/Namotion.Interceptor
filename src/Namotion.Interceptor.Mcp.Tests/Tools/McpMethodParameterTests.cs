using System.Collections;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Mcp.Models;
using Namotion.Interceptor.Mcp.Tools;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class McpMethodParameterTests
{
    [Fact]
    public void WhenParameterIsTimeSpan_ThenTypeIsStringWithFormatLabel()
    {
        // Act
        var parameter = McpMethodParameter.Create("position", typeof(TimeSpan));

        // Assert
        Assert.Equal("position", parameter.Name);
        Assert.Equal("string", parameter.Type);
        Assert.Equal("hh:mm:ss", parameter.Format);
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
    public void WhenParameterHasFormatAndEnum_ThenSerializesThem()
    {
        // Act
        var dateJson = JsonSerializer.Serialize(McpMethodParameter.Create("start", typeof(DateTimeOffset)));
        var timeSpanJson = JsonSerializer.Serialize(McpMethodParameter.Create("position", typeof(TimeSpan)));
        var enumJson = JsonSerializer.Serialize(McpMethodParameter.Create("mode", typeof(TestMode)));

        // Assert
        Assert.Equal("""{"name":"start","type":"string","format":"date-time"}""", dateJson);
        Assert.Equal("""{"name":"position","type":"string","format":"hh:mm:ss"}""", timeSpanJson);
        Assert.Equal("""{"name":"mode","type":"string","enum":["Off","On"]}""", enumJson);
    }

    [Fact]
    public void WhenParameterIsRecord_ThenTheSchemaListsItsCamelCaseMembers()
    {
        // Act
        var parameter = McpMethodParameter.Create("notification", typeof(TestNotification));

        // Assert
        Assert.Equal("object", parameter.Type);
        var schema = parameter.Schema!.Value;
        var properties = schema.GetProperty("properties");
        Assert.Equal(["message", "code", "title", "priority"], properties.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["message", "code"], schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal("The text shown to the reader.", properties.GetProperty("message").GetProperty("description").GetString());
        Assert.Equal(["Low", "Normal"], properties.GetProperty("priority").GetProperty("enum").EnumerateArray().Select(name => name.GetString()));
    }

    [Fact]
    public void WhenTypeReferencesItself_ThenTheSchemaIsCreated()
    {
        // Act
        var parameter = McpMethodParameter.Create("node", typeof(TestNode));

        // Assert
        Assert.True(parameter.Schema!.Value.GetProperty("properties").TryGetProperty("next", out _));
    }

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(string))]
    [InlineData(typeof(object))]
    [InlineData(typeof(ArrayList))]
    public void WhenParameterIsNoObjectWithMembers_ThenHasNoSchema(Type type)
    {
        // Act
        var parameter = McpMethodParameter.Create("value", type);

        // Assert
        Assert.Null(parameter.Schema);
    }

    public sealed record TestNotification
    {
        [Description("The text shown to the reader.")]
        public required string Message { get; init; }

        [JsonRequired]
        public int Code { get; init; }

        public string? Title { get; init; }

        public TestPriority Priority { get; init; }
    }

    public enum TestPriority
    {
        Low,
        Normal
    }

    public sealed class TestNode
    {
        public string? Name { get; set; }

        public TestNode? Next { get; set; }
    }

    public enum TestMode
    {
        Off,
        On
    }
}
