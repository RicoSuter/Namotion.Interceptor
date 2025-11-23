using Namotion.Interceptor.Sources.Paths;

namespace Namotion.Interceptor.Sources.Tests;

public class AttributeBasedSourcePathProviderTests
{
    [Fact]
    public void WhenCallingParsePathSegmentsAndPrefix_ThenPrefixIsRemoved()
    {
        // Arrange
        var provider = new AttributeBasedSourcePathProvider("test", ".", "Foo.Bar");
        
        // Act
        var segments = provider.ParsePathSegments("Foo.Bar.Baz.Buz").ToArray();
        
        // Assert
        Assert.Equal(("Baz", null), segments.ElementAt(0));
        Assert.Equal(("Buz", null), segments.ElementAt(1));
    }
}