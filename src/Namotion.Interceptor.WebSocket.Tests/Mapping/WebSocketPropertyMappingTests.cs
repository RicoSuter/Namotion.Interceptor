using Namotion.Interceptor.WebSocket.Mapping;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Mapping;

public class WebSocketPropertyMappingTests
{
    [Fact]
    public void WhenMergingTwoEmptyMappings_ThenReturnsEmptyMapping()
    {
        // Arrange & Act
        var merged = WebSocketPropertyMapping.Merge(new(), new());

        // Assert
        Assert.NotNull(merged);
    }
}
