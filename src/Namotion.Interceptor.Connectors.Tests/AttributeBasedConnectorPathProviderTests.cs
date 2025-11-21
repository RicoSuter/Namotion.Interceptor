using Namotion.Interceptor.Connectors.Paths;

namespace Namotion.Interceptor.Connectors.Tests;

public class AttributeBasedConnectorPathProviderTests
{
    [Fact]
    public void WhenCallingParsePathSegmentsAndPrefix_ThenPrefixIsRemoved()
    {
        // Arrange
        var provider = new AttributeBasedConnectorPathProvider("test", ".", "Foo.Bar");
        
        // Act
        var segments = provider.ParsePathSegments("Foo.Bar.Baz.Buz").ToArray();
        
        // Assert
        Assert.Equal(("Baz", null), segments.ElementAt(0));
        Assert.Equal(("Buz", null), segments.ElementAt(1));
    }
}