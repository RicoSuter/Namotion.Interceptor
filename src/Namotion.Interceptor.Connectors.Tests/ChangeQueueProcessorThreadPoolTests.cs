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

    // How long the dequeue loop stays busy waiting for the flush. A flush that only a steal can start
    // never runs while the global queue is flooded, so this bounds the failure, not the passing run.
    private static readonly TimeSpan FlushStartTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WhenTheDequeueLoopStaysBusyWhileTheGlobalQueueIsBusy_ThenTheFlushStillDelivers()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var firstNameDelivered = new ManualResetEventSlim();
        var flushRanWhileDequeueLoopWasBusy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: property =>
            {
                if (property.Name == nameof(Person.LastName))
                {
                    // Holds the dequeue loop's pool thread, so the flush has to run on another one.
                    flushRanWhileDequeueLoopWasBusy.TrySetResult(firstNameDelivered.Wait(FlushStartTimeout));
                }

                return true;
            },
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.Span)
                {
                    if (change.Property.Name == nameof(Person.FirstName))
                    {
                        firstNameDelivered.Set();
                    }
                }

                return ValueTask.CompletedTask;
            },
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale,
            bufferTime: TimeSpan.FromMilliseconds(8),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        // Captured by the processor's subscription before processing starts, so the loop reaches the
        // blocking filter right after buffering the first change.
        person.FirstName = "first";
        person.LastName = "second";

        using var cancellation = new CancellationTokenSource();
        Task processing;
        bool flushRan;
        using (ThreadPoolGlobalQueueFlood.Start())
        {
            // Act
            processing = processor.ProcessAsync(cancellation.Token);
            flushRan = await flushRanWhileDequeueLoopWasBusy.Task.WaitAsync(TestTimeout);
        }

        await cancellation.CancelAsync();
        await processing.WaitAsync(TestTimeout);

        // Assert
        Assert.True(flushRan, "The flush should start on another pool thread while the dequeue loop is busy.");
    }
}
