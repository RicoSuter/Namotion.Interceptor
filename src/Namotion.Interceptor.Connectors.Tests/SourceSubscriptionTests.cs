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
