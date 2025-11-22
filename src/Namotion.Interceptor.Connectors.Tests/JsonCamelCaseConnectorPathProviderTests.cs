using Namotion.Interceptor.Connectors.Paths;

namespace Namotion.Interceptor.Connectors.Tests;

public class JsonCamelCaseConnectorPathProviderTests
{
    [Theory]
    [InlineData("FirstName", "firstName")]
    [InlineData("LastName", "lastName")]
    [InlineData("ID", "iD")]
    [InlineData("A", "a")]
    [InlineData("Ab", "ab")]
    [InlineData("ABC", "aBC")]
    [InlineData("", "")]
    public void ConvertToSourcePath_ConvertsCorrectly(string input, string expected)
    {
        // Act
        var result = JsonCamelCaseConnectorPathProvider.ConvertToSourcePath(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("firstName", "FirstName")]
    [InlineData("lastName", "LastName")]
    [InlineData("iD", "ID")]
    [InlineData("a", "A")]
    [InlineData("ab", "Ab")]
    [InlineData("aBC", "ABC")]
    [InlineData("", "")]
    public void ConvertFromSourcePath_ConvertsCorrectly(string input, string expected)
    {
        // Act
        var result = JsonCamelCaseConnectorPathProvider.ConvertFromSourcePath(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParsePathSegments_ConvertsToPascalCase()
    {
        // Arrange
        var provider = JsonCamelCaseConnectorPathProvider.Instance;

        // Act
        var segments = provider.ParsePathSegments("person.firstName.value").ToList();

        // Assert
        Assert.Equal(3, segments.Count);
        Assert.Equal("Person", segments[0].segment);
        Assert.Equal("FirstName", segments[1].segment);
        Assert.Equal("Value", segments[2].segment);
    }

    [Fact]
    public void ParsePathSegments_HandlesIndexes()
    {
        // Arrange
        var provider = JsonCamelCaseConnectorPathProvider.Instance;

        // Act
        var segments = provider.ParsePathSegments("items[0].name").ToList();

        // Assert
        Assert.Equal(2, segments.Count);
        Assert.Equal("Items", segments[0].segment);
        Assert.Equal(0, segments[0].index);
        Assert.Equal("Name", segments[1].segment);
        Assert.Null(segments[1].index);
    }

    [Fact]
    public void ParsePathSegments_HandlesStringIndexes()
    {
        // Arrange
        var provider = JsonCamelCaseConnectorPathProvider.Instance;

        // Act
        var segments = provider.ParsePathSegments("dictionary[key].value").ToList();

        // Assert
        Assert.Equal(2, segments.Count);
        Assert.Equal("Dictionary", segments[0].segment);
        Assert.Equal("key", segments[0].index);
        Assert.Equal("Value", segments[1].segment);
    }

    [Fact]
    public void ParsePathSegments_HandlesSingleSegment()
    {
        // Arrange
        var provider = JsonCamelCaseConnectorPathProvider.Instance;

        // Act
        var segments = provider.ParsePathSegments("propertyName").ToList();

        // Assert
        Assert.Single(segments);
        Assert.Equal("PropertyName", segments[0].segment);
        Assert.Null(segments[0].index);
    }

    [Fact]
    public void ParsePathSegments_HandlesEmptyPath()
    {
        // Arrange
        var provider = JsonCamelCaseConnectorPathProvider.Instance;

        // Act
        var segments = provider.ParsePathSegments("").ToList();

        // Assert
        Assert.Empty(segments);
    }
}
