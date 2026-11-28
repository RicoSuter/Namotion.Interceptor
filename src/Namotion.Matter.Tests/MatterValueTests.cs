using System.Text.Json;

namespace Namotion.Matter.Tests;

public class MatterValueTests
{
    private static MatterValue Parse(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    [Theory]
    [InlineData("null", MatterValueKind.Null)]
    [InlineData("true", MatterValueKind.Boolean)]
    [InlineData("1.5", MatterValueKind.Number)]
    [InlineData("\"text\"", MatterValueKind.String)]
    [InlineData("[1,2]", MatterValueKind.List)]
    [InlineData("{\"0\":1}", MatterValueKind.Struct)]
    public void WhenCreatedFromJson_ThenKindMatches(string json, MatterValueKind expected)
    {
        // Act
        var value = Parse(json);

        // Assert
        Assert.Equal(expected, value.Kind);
    }

    [Fact]
    public void WhenDefault_ThenValueIsNull()
    {
        // Act
        var value = default(MatterValue);

        // Assert
        Assert.True(value.IsNull);
        Assert.Equal("null", value.ToString());
    }

    [Fact]
    public void WhenNumberIsUInt64Maximum_ThenItIsReadExactly()
    {
        // Act
        var value = Parse("18446744073709551615");

        // Assert
        Assert.Equal(ulong.MaxValue, value.GetUInt64());
    }

    [Fact]
    public void WhenStructIsKeyedByTag_ThenFieldIsFoundByTag()
    {
        // Arrange
        var value = Parse("{\"0\":22,\"1\":4}");

        // Act
        var found = value.TryGetField(1, "revision", out var field);

        // Assert
        Assert.True(found);
        Assert.Equal(4, field.GetInt64());
    }

    [Fact]
    public void WhenStructIsKeyedByName_ThenFieldIsFoundByName()
    {
        // Arrange
        var value = Parse("{\"newPosition\":1}");

        // Act
        var found = value.TryGetField(0, "newPosition", out var field);

        // Assert
        Assert.True(found);
        Assert.Equal(1, field.GetInt64());
    }

    [Fact]
    public void WhenFieldIsMissing_ThenTryGetFieldFails()
    {
        // Arrange
        var value = Parse("{\"0\":1}");

        // Act
        var found = value.TryGetField(5, "other", out var field);

        // Assert
        Assert.False(found);
        Assert.True(field.IsNull);
    }

    [Fact]
    public void WhenValueIsNotStruct_ThenTryGetFieldFails()
    {
        // Act
        var found = Parse("[1]").TryGetField(0, "first", out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public void WhenEnumeratingList_ThenItemsAreReturnedInOrder()
    {
        // Arrange
        var value = Parse("[31,40,29]");

        // Act
        var items = new List<long>();
        foreach (var item in value.EnumerateList())
        {
            items.Add(item.GetInt64());
        }

        // Assert
        Assert.Equal([31, 40, 29], items);
        Assert.Equal(3, value.GetListLength());
    }

    [Fact]
    public void WhenOctetStringIsBase64_ThenBytesAreDecoded()
    {
        // Act
        var bytes = Parse("\"AQID\"").GetBytes();

        // Assert
        Assert.Equal([1, 2, 3], bytes);
    }

    [Fact]
    public void WhenReadingWrongKind_ThenInvalidOperationExceptionIsThrown()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => Parse("\"text\"").GetBoolean());
    }
}
