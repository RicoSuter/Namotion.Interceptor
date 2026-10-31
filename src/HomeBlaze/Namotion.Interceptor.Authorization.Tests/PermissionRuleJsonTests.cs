using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Namotion.Interceptor.Authorization.Tests;

public class PermissionRuleJsonTests
{
    [Fact]
    public void WhenRuleHasEverySelector_ThenItRoundTrips()
    {
        // Arrange
        var rule = new PermissionRule
        {
            Paths = ["/Home/Kitchen", "/Line/Machines[*]"],
            Types = ["ILight"],
            Members = ["TurnOn", "Brightness"],
            Tags = ["state"],
            Actions = [PermissionAction.Read, new PermissionAction("execute")]
        };

        // Act
        var json = JsonSerializer.Serialize(rule);
        var roundTrip = JsonSerializer.Deserialize<PermissionRule>(json);

        // Assert
        Assert.Equal(
            """{"paths":["/Home/Kitchen","/Line/Machines[*]"],"types":["ILight"],"members":["TurnOn","Brightness"],"tags":["state"],"actions":["read","execute"]}""",
            json);
        Assert.Equal(rule, roundTrip);
    }

    [Fact]
    public void WhenSelectorsAreEmpty_ThenTheyAreOmittedAndReadBackAsEmpty()
    {
        // Arrange
        var rule = new PermissionRule { Actions = [PermissionAction.Write] };

        // Act
        var json = JsonSerializer.Serialize(rule);
        var roundTrip = JsonSerializer.Deserialize<PermissionRule>(json)!;

        // Assert
        Assert.Equal("""{"actions":["write"]}""", json);
        Assert.Equal(rule, roundTrip);
        Assert.True(roundTrip.Paths.IsEmpty);
        Assert.True(roundTrip.Types.IsEmpty);
        Assert.True(roundTrip.Members.IsEmpty);
        Assert.True(roundTrip.Tags.IsEmpty);
    }

    [Fact]
    public void WhenRuleHasNoActions_ThenActionsAreWrittenAsEmptyArray()
    {
        // Arrange
        var rule = new PermissionRule { Tags = ["state"] };

        // Act
        var json = JsonSerializer.Serialize(rule);
        var roundTrip = JsonSerializer.Deserialize<PermissionRule>(json);

        // Assert
        Assert.Equal("""{"tags":["state"],"actions":[]}""", json);
        Assert.Equal(rule, roundTrip);
    }

    [Fact]
    public void WhenNamingPolicyIsPascalCase_ThenRuleIsStillWrittenInCamelCase()
    {
        // Arrange
        var options = new JsonSerializerOptions { PropertyNamingPolicy = null };
        var rule = new PermissionRule { Tags = ["state"], Actions = [PermissionAction.Read] };

        // Act
        var json = JsonSerializer.Serialize(rule, options);

        // Assert
        Assert.Equal("""{"tags":["state"],"actions":["read"]}""", json);
    }

    [Fact]
    public void WhenPropertyNamesDifferInCase_ThenTheyAreRead()
    {
        // Arrange
        const string json = """{ "PATHS": ["/Home"], "Types": ["ILight"], "mEmBeRs": ["TurnOn"], "Tags": ["state"], "Actions": ["write"] }""";

        // Act
        var rule = JsonSerializer.Deserialize<PermissionRule>(json);

        // Assert
        Assert.Equal(new PermissionRule
        {
            Paths = ["/Home"],
            Types = ["ILight"],
            Members = ["TurnOn"],
            Tags = ["state"],
            Actions = [PermissionAction.Write]
        }, rule);
    }

    [Fact]
    public void WhenPropertyNameIsEscaped_ThenItIsRead()
    {
        // Arrange
        const string json = """{ "tags": ["state"], "actions": ["read"] }""";

        // Act
        var rule = JsonSerializer.Deserialize<PermissionRule>(json)!;

        // Assert
        Assert.Equal(["state"], rule.Tags.AsEnumerable());
    }

    [Theory]
    [InlineData("""{ "path": ["/Home"], "actions": ["read"] }""", "path")]
    [InlineData("""{ "actions": ["read"], "selectorWithAVeryLongNameThatDoesNotFitTheStackBufferOfTheConverterAtAll": [] }""", "selectorWithAVeryLongNameThatDoesNotFitTheStackBufferOfTheConverterAtAll")]
    public void WhenPropertyIsUnknown_ThenReadingThrowsNamingIt(string json, string propertyName)
    {
        // Act & Assert
        var exception = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PermissionRule>(json));
        Assert.Contains($"'{propertyName}'", exception.Message);
    }

    [Fact]
    public void WhenPropertyIsRepeated_ThenReadingThrowsNamingIt()
    {
        // Arrange
        const string json = """{ "paths": ["/Home/Kitchen"], "Paths": ["/"], "actions": ["read"] }""";

        // Act & Assert
        var exception = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PermissionRule>(json));
        Assert.Contains("'Paths'", exception.Message);
    }

    [Theory]
    [InlineData("paths")]
    [InlineData("types")]
    [InlineData("members")]
    [InlineData("tags")]
    [InlineData("actions")]
    public void WhenArrayIsNull_ThenReadingThrows(string propertyName)
    {
        // Arrange
        var json = $$"""{ "{{propertyName}}": null }""";

        // Act & Assert
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PermissionRule>(json));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"rule\"")]
    [InlineData("""{ "paths": "/Home" }""")]
    [InlineData("""{ "paths": [1] }""")]
    [InlineData("""{ "paths": [null] }""")]
    [InlineData("""{ "tags": [["state"]] }""")]
    [InlineData("""{ "actions": [""] }""")]
    [InlineData("""{ "actions": [{ "name": "read" }] }""")]
    public void WhenTokenHasWrongType_ThenReadingThrows(string json)
    {
        // Act & Assert
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PermissionRule>(json));
    }

    [Fact]
    public void WhenRuleIsNull_ThenItReadsAndWritesAsNull()
    {
        // Act
        var rule = JsonSerializer.Deserialize<PermissionRule?>("null");
        var json = JsonSerializer.Serialize<PermissionRule?>(null);

        // Assert
        Assert.Null(rule);
        Assert.Equal("null", json);
    }

    [Fact]
    public void WhenResolverSkipsEveryUnmarkedProperty_ThenRulesInsideAnObjectAreStillWritten()
    {
        // Arrange
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object)
            {
                return;
            }

            foreach (var property in typeInfo.Properties)
            {
                if (property.AttributeProvider?.IsDefined(typeof(PersistedAttribute), inherit: true) != true)
                {
                    property.ShouldSerialize = (_, _) => false;
                }
            }
        });
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        var holder = new RuleHolder
        {
            Rules =
            [
                new PermissionRule { Tags = ["state"], Actions = [PermissionAction.Read] },
                new PermissionRule { Paths = ["/Home/Bedroom"], Types = ["ILight"], Actions = [PermissionAction.Write] }
            ],
            Comment = "not persisted"
        };

        // Act
        var json = JsonSerializer.Serialize(holder, options);
        var roundTrip = JsonSerializer.Deserialize<RuleHolder>(json, options)!;

        // Assert
        Assert.Equal(
            """{"Rules":[{"tags":["state"],"actions":["read"]},{"paths":["/Home/Bedroom"],"types":["ILight"],"actions":["write"]}]}""",
            json);
        Assert.Equal(holder.Rules.AsEnumerable(), roundTrip.Rules.AsEnumerable());
    }

    [AttributeUsage(AttributeTargets.Property)]
    private sealed class PersistedAttribute : Attribute;

    private sealed class RuleHolder
    {
        [Persisted]
        public ImmutableArray<PermissionRule> Rules { get; set; } = [];

        public string? Comment { get; set; }
    }
}
