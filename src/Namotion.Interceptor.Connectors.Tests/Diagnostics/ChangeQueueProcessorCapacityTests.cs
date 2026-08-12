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

    [Fact]
    public void WhenMaxQueueDepthIsZero_ThenTheMessageNamesBothRemedies()
    {
        // Arrange
        // A caller asking for a zero bound is usually asking for no buffering, and the throw lands
        // inside a connector's retry loop, which catches it and tries again, so the message is all they
        // get: it has to point at the unbounded queue and at the path that buffers nothing.
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

        Assert.Contains("null", exception.Message);
        Assert.Contains("buffer time of zero", exception.Message);
    }
}
