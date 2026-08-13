using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

public class ChangeQueueProcessorCapacityTests
{
    [Fact]
    public void WhenMaxQueueDepthIsZeroOnTheBufferedPath_ThenConstructionThrowsWithBothRemedies()
    {
        // Arrange
        // A caller asking for a zero bound is usually asking for no buffering, and the throw lands
        // inside a connector's retry loop, which catches it and tries again, so the message is all they
        // get: it has to point at the unbounded queue and at the path that buffers nothing.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new Person(context);
        using var source = new TestSubjectSource(subject, context, NullLogger.Instance);
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
        Assert.Contains("null", exception.Message);
        Assert.Contains("buffer time of zero", exception.Message);
    }

    [Fact]
    public void WhenMaxQueueDepthIsZeroOnTheImmediatePath_ThenConstructionSucceeds()
    {
        // Arrange
        // The second remedy the message above names has to be one the caller can actually take: the
        // immediate path writes each change as it is dequeued and never fills the queue the bound
        // applies to, so the bound is not read there and the same pair of arguments is accepted.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new Person(context);
        using var source = new TestSubjectSource(subject, context, NullLogger.Instance);
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using var processor = new ChangeQueueProcessor(
            source,
            subscription,
            _ => true,
            (_, _) => ValueTask.CompletedTask,
            ChangeDeliveryRule.SourceValuesMayBeStale,
            TimeSpan.Zero,
            maxQueueDepth: 0,
            logger: NullLogger.Instance);

        // Assert
        Assert.Equal(0, processor.QueueDepth);
        Assert.Equal(0L, processor.DropCount);
    }
}
