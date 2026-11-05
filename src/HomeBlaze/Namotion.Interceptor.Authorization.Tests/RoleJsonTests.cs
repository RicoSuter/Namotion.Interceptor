using System.Text.Json;

namespace Namotion.Interceptor.Authorization.Tests;

public class RoleJsonTests
{
    [Fact]
    public void WhenRoleIsDeserialized_ThenMissingListsAreEmptyAndActionsAreNames()
    {
        // Arrange
        const string json = """
            {
              "name": "LightOperator",
              "includes": ["FullReader"],
              "allow": [
                { "types": ["ILight"], "members": ["TurnOn", "Brightness"], "actions": ["write", "execute"] }
              ],
              "deny": [
                { "paths": ["/Rooms[Bedroom]"], "actions": ["write"] }
              ]
            }
            """;

        // Act
        var role = JsonSerializer.Deserialize<AuthorizationRole>(json, JsonSerializerOptions.Web)!;

        // Assert
        Assert.Equal("LightOperator", role.Name);
        Assert.Equal(["FullReader"], role.Includes.AsEnumerable());
        var allow = Assert.Single(role.Allow);
        Assert.Equal(["ILight"], allow.Types.AsEnumerable());
        Assert.Equal(["TurnOn", "Brightness"], allow.Members.AsEnumerable());
        Assert.Equal([PermissionAction.Write, new PermissionAction("execute")], allow.Actions.AsEnumerable());
        Assert.True(allow.Paths.IsEmpty);
        Assert.True(allow.Tags.IsEmpty);
        var deny = Assert.Single(role.Deny);
        Assert.Equal(["/Rooms[Bedroom]"], deny.Paths.AsEnumerable());
    }

    [Fact]
    public void WhenRoleIsSerialized_ThenItRoundTrips()
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "FullReader",
            Allow = [new PermissionRule { Tags = ["state"], Actions = [PermissionAction.Read, new PermissionAction("history")] }]
        };

        // Act
        var json = JsonSerializer.Serialize(role, JsonSerializerOptions.Web);
        var roundTrip = JsonSerializer.Deserialize<AuthorizationRole>(json, JsonSerializerOptions.Web)!;

        // Assert
        Assert.Contains("\"actions\":[\"read\",\"history\"]", json);
        Assert.Equal(role.Name, roundTrip.Name);
        Assert.Equal(role.Allow[0].Tags.AsEnumerable(), roundTrip.Allow[0].Tags.AsEnumerable());
        Assert.Equal(role.Allow[0].Actions.AsEnumerable(), roundTrip.Allow[0].Actions.AsEnumerable());
    }

    [Fact]
    public void WhenRoleHasOnlyAName_ThenItsListsAreEmptyAndANamelessRoleIsRejected()
    {
        // Act
        var role = JsonSerializer.Deserialize<AuthorizationRole>("""{"name":"Empty"}""", JsonSerializerOptions.Web)!;

        // Assert
        Assert.True(role.Includes.IsEmpty);
        Assert.True(role.Allow.IsEmpty);
        Assert.True(role.Deny.IsEmpty);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AuthorizationRole>("{}", JsonSerializerOptions.Web));
    }

    [Theory]
    [InlineData("""{ "name": "Op", "allow": [{ "actions": ["read"] }], "denny": [{ "actions": ["read"] }] }""")]
    [InlineData("""{ "name": "Op", "allow": [{ "actions": ["read"] }], "Denies": [{ "actions": ["read"] }] }""")]
    public void WhenARoleHasAnUnknownProperty_ThenDeserializationFails(string json)
    {
        // Act & Assert
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AuthorizationRole>(json, JsonSerializerOptions.Web));
    }

    [Fact]
    public void WhenARoleWithoutListsIsDeserializedWithSourceGeneration_ThenTheListsAreEmptyAndItRoundTrips()
    {
        // Arrange
        const string json = """{ "name": "Reader" }""";

        // Act
        var role = JsonSerializer.Deserialize(json, RoleJsonContext.Default.AuthorizationRole)!;
        var roundTripped = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(role, RoleJsonContext.Default.AuthorizationRole), RoleJsonContext.Default.AuthorizationRole)!;

        // Assert
        Assert.True(role.Includes.IsEmpty);
        Assert.True(role.Allow.IsEmpty);
        Assert.True(role.Deny.IsEmpty);
        Assert.Equal(role, roundTripped);
    }

    [Fact]
    public void WhenARoleHasAnUnknownPropertyWithSourceGeneration_ThenDeserializationFails()
    {
        // Act & Assert
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{ "name": "Op", "denny": [] }""", RoleJsonContext.Default.AuthorizationRole));
    }

    [Fact]
    public void WhenARoleIsCreatedWithDefaultLists_ThenTheListsAreEmpty()
    {
        // Act
        var role = new AuthorizationRole { Name = "Reader", Includes = default, Allow = default, Deny = default };

        // Assert
        Assert.True(role.Includes.IsEmpty);
        Assert.True(role.Allow.IsEmpty);
        Assert.True(role.Deny.IsEmpty);
    }

    [Fact]
    public void WhenARoleListIsNull_ThenDeserializationFails()
    {
        // Act & Assert
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AuthorizationRole>("""{ "name": "Op", "deny": null }""", JsonSerializerOptions.Web));
    }
}
