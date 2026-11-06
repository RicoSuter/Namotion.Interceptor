using System.Text.Json;
using Namotion.Interceptor.Authorization.Tests.Models;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class PermissionActionTests
{
    [Fact]
    public void WhenNamesDifferInCase_ThenActionsAreEqual()
    {
        // Arrange
        var lower = new PermissionAction("history");
        var upper = new PermissionAction("HISTORY");

        // Act
        var lowerHashCode = lower.GetHashCode();
        var upperHashCode = upper.GetHashCode();
        var areEqualByOperator = lower == upper;

        // Assert
        Assert.Equal(lower, upper);
        Assert.Equal(lowerHashCode, upperHashCode);
        Assert.True(areEqualByOperator);
    }

    [Fact]
    public void WhenNamesDiffer_ThenActionsAreNotEqual()
    {
        // Arrange
        var read = PermissionAction.Read;
        var write = PermissionAction.Write;

        // Act
        var areEqual = read.Equals(write);

        // Assert
        Assert.False(areEqual);
        Assert.NotEqual(read, write);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void WhenNameIsEmpty_ThenConstructorThrows(string name)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new PermissionAction(name));
    }

    [Fact]
    public void WhenSerialized_ThenActionIsItsName()
    {
        // Arrange
        PermissionAction[] actions = [PermissionAction.Read, new PermissionAction("history")];

        // Act
        var json = JsonSerializer.Serialize(actions);
        var roundTrip = JsonSerializer.Deserialize<PermissionAction[]>(json);

        // Assert
        Assert.Equal("""["read","history"]""", json);
        Assert.Equal(actions, roundTrip);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("\"\"")]
    public void WhenJsonIsNotAName_ThenDeserializationThrows(string json)
    {
        // Act & Assert
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PermissionAction>(json));
    }

    [Fact]
    public void WhenConvertedToString_ThenItIsTheName()
    {
        // Act
        var text = PermissionAction.Read.ToString();

        // Assert
        Assert.Equal("read", text);
    }

    [Fact]
    public void WhenActionIsDefault_ThenNameIsEmptyAndSerializationThrows()
    {
        // Arrange
        var action = default(PermissionAction);

        // Act
        var name = action.Name;
        var text = action.ToString();
        var hashCode = action.GetHashCode();

        // Assert
        Assert.Equal(string.Empty, name);
        Assert.Equal(string.Empty, text);
        Assert.Equal(hashCode, default(PermissionAction).GetHashCode());
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(action));
    }

    [Fact]
    public void WhenActionIsDefault_ThenNoPolicyKnowsIt()
    {
        // Arrange
        var member = Member(Create().Wifi!, nameof(Wifi.Ssid));
        var policy = Policies.Compile(Policies.Role("Reader"));

        // Act
        var isAllowedByDenyAll = AuthorizationPolicy.DenyAll.IsAllowed(member, default);

        // Assert
        Assert.False(isAllowedByDenyAll);
        Assert.Throws<ArgumentException>(() => policy.IsAllowed(member, default));
        var exception = Assert.Throws<AuthorizationException>(
            () => AuthorizationPolicy.Compile([], [], [], [default], TestTags.Default, Policies.SlashPaths));
        Assert.Equal("A known action has no name.", exception.Message);
    }
}
