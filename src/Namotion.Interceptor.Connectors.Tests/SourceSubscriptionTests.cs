using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Pins the disposal contract documented on <see cref="SourceSubscription.Dispose"/>: an in-flight
/// handler is not interrupted by Dispose, and an event still queued (dequeued but not yet handled)
/// at the moment the drain loop observes disposal is dropped rather than delivered.
/// </summary>
public class SourceSubscriptionTests
{
    [Fact]
    public void WhenDisposedWhileAHandlerIsInFlight_ThenDisposeDoesNotWaitAndTheHandlerStillCompletes()
    {
        // Arrange
        var handlerEntered = new ManualResetEventSlim(false);
        var releaseHandler = new ManualResetEventSlim(false);
        var handlerCompleted = new ManualResetEventSlim(false);

        using var subscription = new SourceSubscription(
            _ =>
            {
                handlerEntered.Set();
                releaseHandler.Wait(TimeSpan.FromSeconds(10));
                handlerCompleted.Set();
            },
            ImmutableArray<ISubjectSource>.Empty,
            _ => { },
            null);

        subscription.Enqueue(CreateEvent());
        Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(10)));

        // Act
        subscription.Dispose();

        // Assert
        // Dispose does not block on the in-flight handler: at this point it is still parked on
        // releaseHandler, so if Dispose had waited for it, handlerCompleted could not be unset here.
        Assert.False(handlerCompleted.IsSet);
        releaseHandler.Set();
        Assert.True(handlerCompleted.Wait(TimeSpan.FromSeconds(10)),
            "An in-flight handler must run to completion even after Dispose() has returned.");
    }

    [Fact]
    public async Task WhenAnEventIsQueuedButNotYetDrainedAtDisposal_ThenItIsNeverDelivered()
    {
        // Arrange
        var deliveredCount = 0;
        var firstHandlerEntered = new ManualResetEventSlim(false);
        var releaseFirstHandler = new ManualResetEventSlim(false);

        var subscription = new SourceSubscription(
            _ =>
            {
                if (Interlocked.Increment(ref deliveredCount) == 1)
                {
                    firstHandlerEntered.Set();
                    releaseFirstHandler.Wait(TimeSpan.FromSeconds(10));
                }
            },
            ImmutableArray<ISubjectSource>.Empty,
            _ => { },
            null);

        subscription.Enqueue(CreateEvent());
        Assert.True(firstHandlerEntered.Wait(TimeSpan.FromSeconds(10)));

        // The second event sits in the internal queue, not yet drained, while the first handler
        // blocks inside the drain loop.
        subscription.Enqueue(CreateEvent());

        // Act
        subscription.Dispose();
        releaseFirstHandler.Set();

        // Assert
        // TryDequeue always removes an item from the internal queue before the disposed check
        // decides whether to deliver it, so "the queue is empty" is a race-free proxy for "the
        // drain loop has finished deciding the fate of every item that was ever enqueued" -
        // whether or not disposal caused any of them to be dropped along the way.
        await AsyncTestHelpers.WaitUntilAsync(() => IsInternalQueueEmpty(subscription));
        Assert.Equal(1, deliveredCount);
    }

    [Fact]
    public async Task WhenManyProducersEnqueueConcurrentlyWhileDraining_ThenEveryEventIsEventuallyDelivered()
    {
        // Arrange
        // Best-effort regression coverage for the drain handoff fixed by using Interlocked.Exchange
        // instead of Volatile.Write when clearing _draining (see Drain's comment on that line): the
        // actual store-load reordering defect reproduced only once in roughly 501 million aligned
        // attempts on ARM64, far beyond what a unit test can afford to run, so this cannot reliably
        // force that exact hardware race. It does exercise the same enqueue-during-drain-exit
        // handoff under heavy concurrency, many times over, which would also catch a logical
        // regression in the re-check loop (e.g. dropping the CompareExchange re-check, or reverting
        // to a plain read) - stranding an event here fails the test instead of hanging it, because
        // the wait below is bounded.
        const int producerCount = 8;
        const int eventsPerProducer = 20_000;
        const int totalEvents = producerCount * eventsPerProducer;

        var delivered = 0;
        using var allDelivered = new ManualResetEventSlim(false);

        var subscription = new SourceSubscription(
            _ =>
            {
                if (Interlocked.Increment(ref delivered) == totalEvents)
                {
                    allDelivered.Set();
                }
            },
            ImmutableArray<ISubjectSource>.Empty,
            _ => { },
            null);

        var producers = Enumerable.Range(0, producerCount)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < eventsPerProducer; i++)
                {
                    subscription.Enqueue(CreateEvent());
                }
            }))
            .ToArray();

        // Act
        await Task.WhenAll(producers);

        // Assert
        Assert.True(allDelivered.Wait(TimeSpan.FromSeconds(60)),
            $"Expected all {totalEvents} events to be delivered; only {Volatile.Read(ref delivered)} were - " +
            "an event was stranded.");

        subscription.Dispose();
    }

    private static bool IsInternalQueueEmpty(SourceSubscription subscription)
    {
        var field = typeof(SourceSubscription).GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var queue = (ConcurrentQueue<SourceEvent>)field.GetValue(subscription)!;
        return queue.IsEmpty;
    }

    private static SourceEvent CreateEvent()
    {
        var source = new TestStateSource(new Person());
        return new SourceEvent(
            SourceEventKind.SourceRegistered, source, null, SourceState.Connecting, SourceState.Connecting, DateTimeOffset.UtcNow);
    }
}
