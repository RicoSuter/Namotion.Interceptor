using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectSourceBaseOverflowTests
{
    [Fact]
    public async Task WhenSourceConfiguresBoundedQueue_ThenOverflowHandlerFires()
    {
        // Arrange
        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.WithPropertyChangeQueue();

        var subject = new Person(context);

        var overflows = new System.Collections.Concurrent.ConcurrentQueue<ChangeQueueOverflow>();
        using var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            CreateChangeQueueConfigurationOverride = () => new ChangeQueueProcessorConfiguration
            {
                // Large buffer time keeps the periodic flush from draining the queue, so the bound
                // is exercised purely by enqueue-side overflow.
                BufferTime = TimeSpan.FromMinutes(10),
                OverflowBehavior = OverflowBehavior.DropOldest,
                MaxQueueSize = 2,
                OverflowHandler = overflow => overflows.Enqueue(overflow),
            },
        };

        // The processor only handles changes to properties whose source is this source, and ignores
        // changes that originated from this source. Claim ownership, then mutate locally (source null)
        // so the changes are queued for outbound writing.
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        using var cancellation = new CancellationTokenSource();
        await source.StartAsync(cancellation.Token);

        // Act - continuously produce unique changes until the bounded queue overflows. Looping covers
        // the window between StartAsync returning and the background processor's subscription going
        // live, so no change is missed because of startup timing. Unique values keep the equality
        // interceptor from coalescing the changes away.
        var producer = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested && overflows.Count < 3)
            {
                subject.FirstName = Guid.NewGuid().ToString("N");
                await Task.Yield();
            }
        });

        await AsyncTestHelpers.WaitUntilAsync(
            () => overflows.Count >= 3,
            message: "Three overflow events should be reported through the seam");

        // Assert
        Assert.All(overflows, overflow => Assert.Equal(OverflowBehavior.DropOldest, overflow.OverflowBehavior));

        // Cleanup
        await cancellation.CancelAsync();
        await producer;
        await source.StopAsync(CancellationToken.None);
    }
}
