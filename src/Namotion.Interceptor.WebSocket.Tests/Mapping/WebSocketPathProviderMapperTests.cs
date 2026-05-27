using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.WebSocket.Mapping;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Mapping;

public class WebSocketPathProviderMapperTests
{
    [Fact]
    public void WhenPathProviderIncludes_ThenMapperReturnsEmptyMapping()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new TestRoot(context);
        var prop = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestRoot.Name))!;
        var mapper = new WebSocketPathProviderMapper(DefaultPathProvider.Instance);

        // Act
        var found = mapper.TryGetMapping(prop, out var mapping);

        // Assert
        Assert.True(found);
        Assert.NotNull(mapping);
    }
}
