using System.Text.Json;

namespace Namotion.Interceptor.Authorization.Tests;

public class PermissionActionTests
{
    [Fact]
    public void WhenNamesDifferInCase_ThenActionsAreEqual()
    {
        // Arrange
        var lower = new PermissionAction("history");
        var upper = new PermissionAction("HISTORY");

        // Act & Assert
        Assert.Equal(lower, upper);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
        Assert.True(lower == upper);
    }

    [Fact]
    public void WhenNamesDiffer_ThenActionsAreNotEqual()
    {
        // Arrange
        var read = PermissionAction.Read;
        var write = PermissionAction.Write;

        // Act & Assert
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
        // Act & Assert
        Assert.Equal("read", PermissionAction.Read.ToString());
    }
}
