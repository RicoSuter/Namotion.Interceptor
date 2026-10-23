using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;

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
        var handlerEntered = new ManualResetEventSlim(false);
        using var subscription = new SourceSubscription(
            _ => handlerEntered.Set(),
            ImmutableArray<ISubjectSource>.Empty,
            _ => { },
            new SourceMonitor());

        var stopFlood = new StrongBox<int>();
        for (var i = 0; i < Math.Max(64, ThreadPool.ThreadCount * 4); i++)
        {
            QueueFloodItem(stopFlood);
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
            Volatile.Write(ref stopFlood.Value, 1);
        }
    }

    /// <summary>
    /// Queues a work item that occupies a worker briefly and then queues itself again, so the
    /// global queue never runs empty until <paramref name="stop"/> is set.
    /// </summary>
    private static void QueueFloodItem(StrongBox<int> stop) =>
        ThreadPool.UnsafeQueueUserWorkItem(static stop =>
        {
            Thread.Sleep(20);
            if (Volatile.Read(ref stop.Value) == 0)
            {
                QueueFloodItem(stop);
            }
        }, stop, preferLocal: false);

    private static SourceEvent CreateEvent() => new(
        SourceEventKind.SourceRegistered, new TestStateSource(new Person()), null,
        SourceState.Synchronizing, SourceState.Synchronizing, DateTimeOffset.UtcNow);
}
