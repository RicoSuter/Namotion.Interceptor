using System.Collections.Immutable;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Connectors.Tests;

[CollectionDefinition(nameof(SourceSubscriptionSchedulingTests), DisableParallelization = true)]
public class SourceSubscriptionSchedulingCollection;

/// <summary>
/// Runs alone because it floods the thread pool on purpose, which would slow every test running
/// next to it.
/// </summary>
[Collection(nameof(SourceSubscriptionSchedulingTests))]
public class SourceSubscriptionSchedulingTests
{
    [Fact]
    public async Task WhenAPoolWorkerEnqueuesAndThenBlocksWhileThePoolIsBusy_ThenTheEventIsStillDelivered()
    {
        // Arrange
        // A drain queued on the enqueuing worker's local queue runs only once another worker steals
        // it, and a worker steals only when the global queue is empty. Keeping the global queue busy
        // and then blocking the enqueuing worker is what a loaded host does to a producer that raises
        // a source event and carries on, and it stranded the drain until that worker was released.
        using var handlerEntered = new ManualResetEventSlim(false);
        using var subscription = new SourceSubscription(
            _ => handlerEntered.Set(),
            ImmutableArray<ISubjectSource>.Empty,
            _ => { },
            new SourceMonitor());

        var flood = new Flood();
        var floodItemCount = Math.Max(64, ThreadPool.ThreadCount * 4);
        flood.ActiveItems = floodItemCount;
        for (var i = 0; i < floodItemCount; i++)
        {
            QueueFloodItem(flood);
        }

        try
        {
            // Act
            var delivered = await Task.Run(() =>
            {
                subscription.Enqueue(CreateEvent());
                return handlerEntered.Wait(TimeSpan.FromSeconds(10));
            });

            // Assert
            Assert.True(delivered, "The drain of an event enqueued on a busy pool never ran.");
        }
        finally
        {
            // Waits for the flood to drain so none of it is left occupying the pool for the next test.
            Volatile.Write(ref flood.Stop, 1);
            await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref flood.ActiveItems) == 0);
        }
    }

    /// <summary>
    /// Queues a work item that occupies a worker briefly and then queues itself again, so the
    /// global queue never runs empty until <see cref="Flood.Stop"/> is set.
    /// </summary>
    private static void QueueFloodItem(Flood flood) =>
        ThreadPool.UnsafeQueueUserWorkItem(static flood =>
        {
            Thread.Sleep(20);
            if (Volatile.Read(ref flood.Stop) == 0)
            {
                QueueFloodItem(flood);
            }
            else
            {
                Interlocked.Decrement(ref flood.ActiveItems);
            }
        }, flood, preferLocal: false);

    private sealed class Flood
    {
        public int Stop;
        public int ActiveItems;
    }

    private static SourceEvent CreateEvent() => new(
        SourceEventKind.SourceRegistered, new TestStateSource(new Person()), null,
        SourceState.Synchronizing, SourceState.Synchronizing, DateTimeOffset.UtcNow);
}
