using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

public class ChangeQueueProcessorCapacityTests
{
    [Fact]
    public void WhenMaxQueueDepthIsZero_ThenConstructionThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new Person(context);
        var source = new TestSubjectSource(subject, context, NullLogger.Instance);
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ChangeQueueProcessor(
            source,
            subscription,
            _ => true,
            (_, _) => ValueTask.CompletedTask,
            ChangeDeliveryRule.SourceValuesMayBeStale,
            TimeSpan.FromMilliseconds(8),
            maxQueueDepth: 0,
            logger: NullLogger.Instance));

        Assert.Equal("maxQueueDepth", exception.ParamName);
    }
}
