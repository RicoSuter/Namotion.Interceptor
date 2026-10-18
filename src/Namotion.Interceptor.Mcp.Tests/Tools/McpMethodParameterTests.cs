using System.Collections;
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
    public void WhenParameterIsRecord_ThenListsItsMembersWithRequiredAndNullable()
    {
        // Act
        var parameter = McpMethodParameter.Create("notification", typeof(TestNotification));

        // Assert
        Assert.Equal("object", parameter.Type);
        var properties = parameter.Properties!;
        Assert.Equal(["message", "title", "priority"], properties.Select(property => property.Name));
        Assert.True(properties[0].IsRequired);
        Assert.False(properties[0].IsNullable);
        Assert.False(properties[1].IsRequired);
        Assert.True(properties[1].IsNullable);
        Assert.Equal(["Low", "Normal"], properties[2].EnumValues);
    }

    [Fact]
    public void WhenRecordIsSerialized_ThenRequiredAndPropertiesAreWritten()
    {
        // Act
        var json = JsonSerializer.Serialize(McpMethodParameter.Create("notification", typeof(TestNotification)));

        // Assert
        Assert.Equal(
            """{"name":"notification","type":"object","properties":[{"name":"message","type":"string","required":true},{"name":"title","type":"string","nullable":true},{"name":"priority","type":"string","enum":["Low","Normal"]}]}""",
            json);
    }

    [Fact]
    public void WhenTypeReferencesItself_ThenTheNestedMemberHasNoProperties()
    {
        // Act
        var parameter = McpMethodParameter.Create("node", typeof(TestNode));

        // Assert
        var next = Assert.Single(parameter.Properties!, property => property.Name == "next");
        Assert.Equal("object", next.Type);
        Assert.Null(next.Properties);
    }

    [Fact]
    public void WhenParameterIsObject_ThenHasNoProperties()
    {
        // Act
        var parameter = McpMethodParameter.Create("value", typeof(object));

        // Assert
        Assert.Null(parameter.Properties);
    }

    public sealed record TestNotification
    {
        public required string Message { get; init; }

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

    [Fact]
    public void WhenDerivedTypeHidesABaseMember_ThenOnlyTheMostDerivedMemberIsListed()
    {
        // Act
        var parameter = McpMethodParameter.Create("value", typeof(HidingDerived));

        // Assert
        var member = Assert.Single(parameter.Properties!, property => property.Name == "v");
        Assert.Equal("string", member.Type);
    }

    [Fact]
    public void WhenAMemberIsAlwaysIgnored_ThenItIsExcludedButConditionallyIgnoredMembersAreListed()
    {
        // Act
        var parameter = McpMethodParameter.Create("value", typeof(JsonIgnoreFixture));

        // Assert
        Assert.Equal(["visible"], parameter.Properties!.Select(property => property.Name));
    }

    [Fact]
    public void WhenParameterIsACollectionType_ThenHasNoProperties()
    {
        // Act
        var parameter = McpMethodParameter.Create("items", typeof(ArrayList));

        // Assert
        Assert.Null(parameter.Properties);
    }

    [Fact]
    public void WhenNestingReachesTheDepthLimit_ThenTheDeepestMemberHasNoProperties()
    {
        // Act
        var parameter = McpMethodParameter.Create("level1", typeof(Level1));

        // Assert
        var next1 = Assert.Single(parameter.Properties!, property => property.Name == "next");
        Assert.NotNull(next1.Properties);

        var next2 = Assert.Single(next1.Properties!, property => property.Name == "next");
        Assert.NotNull(next2.Properties);

        var next3 = Assert.Single(next2.Properties!, property => property.Name == "next");
        Assert.Equal("object", next3.Type);
        Assert.Null(next3.Properties);
    }

    public class HidingBase
    {
        public int V { get; set; }
    }

    public sealed class HidingDerived : HidingBase
    {
        public new string V { get; set; } = string.Empty;
    }

    public sealed class JsonIgnoreFixture
    {
        [JsonIgnore]
        public string? Hidden { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Visible { get; set; }
    }

    public sealed class Level1
    {
        public Level2? Next { get; set; }
    }

    public sealed class Level2
    {
        public Level3? Next { get; set; }
    }

    public sealed class Level3
    {
        public Level4? Next { get; set; }
    }

    public sealed class Level4
    {
        public string? Name { get; set; }
    }

    public enum TestMode
    {
        Off,
        On
    }
}
