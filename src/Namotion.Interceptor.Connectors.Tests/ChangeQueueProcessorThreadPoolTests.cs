using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests;

[Collection(ThreadPoolFloodCollection.Name)]
public class ChangeQueueProcessorThreadPoolTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    // How long the write handler stays blocked waiting for the next drain. A drain that only a steal can
    // start never runs while the global queue is flooded, so this bounds the failure, not the passing run.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WhenAWriteBlocksSynchronouslyWhileTheGlobalQueueIsBusy_ThenTheNextTickStillDrains()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var laterChangeDrained = new ManualResetEventSlim();
        var drainedWhileWriteBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: property =>
            {
                if (property.Name == nameof(Person.LastName))
                {
                    laterChangeDrained.Set();
                }

                return true;
            },
            writeHandler: (changes, _) =>
            {
                if (changes.Span[0].Property.Name == nameof(Person.FirstName))
                {
                    // Written from here rather than by the test, whose continuation could queue behind this
                    // thread. Then holds the thread the flush runs on, so the next drain needs another one.
                    person.LastName = "second";
                    drainedWhileWriteBlocked.TrySetResult(laterChangeDrained.Wait(DrainTimeout));
                }

                return ValueTask.CompletedTask;
            },
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale,
            bufferTime: TimeSpan.FromMilliseconds(8),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);
        bool drained;
        using (ThreadPoolGlobalQueueFlood.Start())
        {
            // Act
            person.FirstName = "first";
            drained = await drainedWhileWriteBlocked.Task.WaitAsync(TestTimeout);
        }

        await cancellation.CancelAsync();
        await processing.WaitAsync(TestTimeout);

        // Assert
        Assert.True(drained, "The next tick should drain on another pool thread while the write blocks.");
    }
}
