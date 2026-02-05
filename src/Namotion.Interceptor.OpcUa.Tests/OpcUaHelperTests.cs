using Namotion.Interceptor.OpcUa;

namespace Namotion.Interceptor.OpcUa.Tests;

public class OpcUaHelperTests
{
    [Theory]
    [InlineData("Sensors[0]", "Sensors", 0, true)]
    [InlineData("Sensors[42]", "Sensors", 42, true)]
    [InlineData("Items[123]", "Items", 123, true)]
    [InlineData("MyProperty[999]", "MyProperty", 999, true)]
    [InlineData("Sensors", null, 0, false)]
    [InlineData("Sensors[]", null, 0, false)]
    [InlineData("Sensors[-1]", null, 0, false)]
    [InlineData("[0]", null, 0, false)]
    [InlineData("Sensors[abc]", null, 0, false)]
    [InlineData("", null, 0, false)]
    public void TryParseCollectionIndex_WithoutPropertyName_ParsesCorrectly(
        string input,
        string? expectedBase,
        int expectedIndex,
        bool expectedResult)
    {
        // Act
        var result = OpcUaHelper.TryParseCollectionIndex(input, out var baseName, out var index);

        // Assert
        Assert.Equal(expectedResult, result);
        if (expectedResult)
        {
            Assert.Equal(expectedBase, baseName);
            Assert.Equal(expectedIndex, index);
        }
    }

    [Theory]
    [InlineData("Sensors[0]", "Sensors", 0, true)]
    [InlineData("Sensors[0]", "Items", 0, false)]
    [InlineData("Items[5]", "Items", 5, true)]
    public void TryParseCollectionIndex_WithPropertyName_MatchesPrefix(
        string input,
        string propertyName,
        int expectedIndex,
        bool expectedResult)
    {
        // Act
        var result = OpcUaHelper.TryParseCollectionIndex(input, propertyName, out var index);

        // Assert
        Assert.Equal(expectedResult, result);
        if (expectedResult)
        {
            Assert.Equal(expectedIndex, index);
        }
    }

    [Theory]
    [InlineData("Root.Collection[2]", 2, 1, "Root.Collection[1]")]
    [InlineData("Root.Collection[5]", 5, 4, "Root.Collection[4]")]
    [InlineData("ns=2;s=Root.Items[3]", 3, 2, "ns=2;s=Root.Items[2]")]
    // Key bug case: nested collections with same index - should only replace FIRST occurrence
    [InlineData("Root.Collection[2].Items[2]", 2, 1, "Root.Collection[1].Items[2]")]
    [InlineData("Root.Collection[2].Nested[2].Deep[2]", 2, 1, "Root.Collection[1].Nested[2].Deep[2]")]
    // Different indices - no replacement needed for inner
    [InlineData("Root.Collection[3].Items[2]", 3, 2, "Root.Collection[2].Items[2]")]
    public void ReindexFirstCollectionIndex_ReplacesOnlyFirstOccurrence(
        string nodeIdStr,
        int oldIndex,
        int newIndex,
        string expected)
    {
        // Act
        var result = OpcUaHelper.ReindexFirstCollectionIndex(nodeIdStr, oldIndex, newIndex);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Root.Collection[1]", 2, 1)] // Index not found
    [InlineData("Root.Property", 0, 1)] // No brackets
    [InlineData("", 0, 1)] // Empty string
    public void ReindexFirstCollectionIndex_ReturnsNull_WhenIndexNotFound(
        string nodeIdStr,
        int oldIndex,
        int newIndex)
    {
        // Act
        var result = OpcUaHelper.ReindexFirstCollectionIndex(nodeIdStr, oldIndex, newIndex);

        // Assert
        Assert.Null(result);
    }
}
