using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.WebSocket.Server;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

public class WebSocketSubjectChangeProcessorTests
{
    [Fact]
    public async Task WhenStartAsyncReturns_ThenTheChangeSubscriptionExists()
    {
        // Arrange: a client welcomed once the host start returns must not miss a change made before
        // the execution runs, so the subscription has to exist by then.
        var context = CreateContext();
        var propertyChangeInterceptor = context.GetService<PropertyChangeInterceptor>();
        using var processor = CreateProcessor(context);
        Assert.True(propertyChangeInterceptor.IsIdle);

        try
        {
            // Act
            await processor.StartAsync(CancellationToken.None);

            // Assert
            Assert.False(propertyChangeInterceptor.IsIdle);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

    private static WebSocketSubjectChangeProcessor CreateProcessor(IInterceptorSubjectContext context)
    {
        var handler = new WebSocketSubjectHandler(
            new TestRoot(context), new WebSocketServerConfiguration(), NullLogger.Instance);
        return new WebSocketSubjectChangeProcessor(handler, NullLogger<WebSocketSubjectChangeProcessor>.Instance);
    }
}
