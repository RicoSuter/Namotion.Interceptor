using Namotion.Interceptor.OpcUa.Graph;

namespace Namotion.Interceptor.OpcUa.Tests.Graph;

public class OpcUaBrowseHelperTests
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
        var result = OpcUaBrowseHelper.TryParseCollectionIndex(input, out var baseName, out var index);

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
        var result = OpcUaBrowseHelper.TryParseCollectionIndex(input, propertyName, out var index);

        // Assert
        Assert.Equal(expectedResult, result);
        if (expectedResult)
        {
            Assert.Equal(expectedIndex, index);
        }
    }
}
