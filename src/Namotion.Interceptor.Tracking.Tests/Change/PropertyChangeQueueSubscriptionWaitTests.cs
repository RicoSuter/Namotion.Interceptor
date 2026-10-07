using System.Runtime.CompilerServices;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class PropertyChangeQueueSubscriptionWaitTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WhenQueueHasChange_ThenWaitCompletesSynchronouslyWithTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        person.FirstName = "John";

        // Act
        var wait = subscription.WaitToDequeueAsync(CancellationToken.None);

        // Assert
        Assert.True(wait.IsCompletedSuccessfully);
        Assert.True(await wait);
        Assert.True(subscription.TryDequeueImmediate(out var change));
        Assert.Equal("John", change.GetNewValue<string?>());
    }

    [Fact]
    public async Task WhenWriteArrivesWhileWaiting_ThenWaitCompletesWithTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var wait = subscription.WaitToDequeueAsync(CancellationToken.None).AsTask();
        Assert.False(wait.IsCompleted);

        // Act
        person.FirstName = "John";

        // Assert
        Assert.True(await wait.WaitAsync(WaitTimeout));
        Assert.True(subscription.TryDequeueImmediate(out var change));
        Assert.Equal("John", change.GetNewValue<string?>());
    }

    [Fact]
    public async Task WhenCancelledWhileWaiting_ThenWaitCompletesWithFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        using var cancellation = new CancellationTokenSource();
        var wait = subscription.WaitToDequeueAsync(cancellation.Token).AsTask();
        Assert.False(wait.IsCompleted);

        // Act
        await cancellation.CancelAsync();

        // Assert
        Assert.False(await wait.WaitAsync(WaitTimeout));
    }

    [Fact]
    public async Task WhenTokenAlreadyCancelled_ThenWaitReturnsFalseEvenWithBufferedChanges()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        person.FirstName = "John";
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var result = await subscription.WaitToDequeueAsync(cancellation.Token);

        // Assert
        Assert.False(result);
        Assert.True(subscription.TryDequeueImmediate(out _));
    }

    [Fact]
    public async Task WhenDisposedWhileWaiting_ThenWaitWakesAndNextWaitReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var subscription = context.CreatePropertyChangeQueueSubscription();
        var wait = subscription.WaitToDequeueAsync(CancellationToken.None).AsTask();
        Assert.False(wait.IsCompleted);

        // Act
        subscription.Dispose();

        // Assert: the woken wait's result may be a spurious true; the next one reports completion.
        await wait.WaitAsync(WaitTimeout);
        Assert.False(subscription.TryDequeueImmediate(out _));
        Assert.False(await subscription.WaitToDequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenDisposedWithBufferedChanges_ThenWaitReturnsTrueUntilDrained()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var subscription = context.CreatePropertyChangeQueueSubscription();
        person.FirstName = "John";

        // Act
        subscription.Dispose();

        // Assert
        Assert.True(await subscription.WaitToDequeueAsync(CancellationToken.None));
        Assert.True(subscription.TryDequeueImmediate(out var change));
        Assert.Equal("John", change.GetNewValue<string?>());
        Assert.False(await subscription.WaitToDequeueAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenEachWriteWaitsForTheConsumer_ThenNoWakeUpIsLost(bool awaitableWait)
    {
        // Arrange: every write lands while the consumer is going back to sleep on an empty queue, and no
        // later write arrives to rescue it, so a single lost wake-up stalls the exchange.
        const int exchangeCount = 10_000;
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        using var stop = new CancellationTokenSource();
        var received = new StrongBox<int>();

        var consumer = awaitableWait
            ? Task.Run(() => ConsumeWithAwaitableWaitAsync(subscription, received, stop.Token))
            : Task.Factory.StartNew(
                () => ConsumeWithBlockingWait(subscription, received, stop.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Act
        var completedExchanges = 0;
        for (var i = 1; i <= exchangeCount; i++)
        {
            person.FirstName = $"Name{i}";
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref received.Value) == i, WaitTimeout))
            {
                break;
            }

            completedExchanges = i;
        }

        await stop.CancelAsync();
        await consumer.WaitAsync(WaitTimeout);

        // Assert
        Assert.Equal(exchangeCount, completedExchanges);
    }

    [Fact]
    public async Task WhenSeveralWritersWakeAnIdleConsumerAtOnce_ThenEveryChangeIsReceived()
    {
        // Arrange: each round starts with the consumer waiting on an empty queue and releases all writers
        // together, so they race to wake it. Exactly one may complete the wait; a second completion would
        // throw from inside a writer's property setter and fail its thread.
        const int writerCount = 4;
        const int roundCount = 2_000;
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var people = Enumerable.Range(0, writerCount).Select(_ => new Person(context)).ToArray();
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        using var cancellation = new CancellationTokenSource();
        var received = new StrongBox<int>();
        var consumer = Task.Run(() => ConsumeWithAwaitableWaitAsync(subscription, received, cancellation.Token));
        using var roundEnd = new Barrier(writerCount, barrier =>
        {
            var expected = (int)(barrier.CurrentPhaseNumber + 1) * writerCount;
            Assert.True(
                SpinWait.SpinUntil(() => Volatile.Read(ref received.Value) == expected, WaitTimeout),
                $"Consumer stalled at {Volatile.Read(ref received.Value)} of {expected} changes.");
        });

        try
        {
            // Act
            await Task.WhenAll(people.Select(person => DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
            {
                for (var round = 0; round < roundCount; round++)
                {
                    person.FirstName = $"Name{round}";

                    // Bounded, so a writer that threw fails the others instead of leaving them parked.
                    Assert.True(roundEnd.SignalAndWait(WaitTimeout), "A writer did not reach the end of the round.");
                }
            })));

            // Assert
            Assert.Equal(writerCount * roundCount, Volatile.Read(ref received.Value));
        }
        finally
        {
            await cancellation.CancelAsync();
            await consumer.WaitAsync(WaitTimeout);
        }
    }

    [Fact]
    public async Task WhenCancellationAndAWriteRaceToEndAWait_ThenItEndsOnceAndTheNextWaitStillWakes()
    {
        // Arrange: the cancellation callback and the writer both try to end the same wait. Only one may
        // complete it; a second completion would throw from the writer's setter or from the cancellation.
        const int iterations = 2_000;
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        for (var i = 0; i < iterations; i++)
        {
            using var cancellation = new CancellationTokenSource();
            var racedWait = subscription.WaitToDequeueAsync(cancellation.Token);

            // Act
            var cancel = Task.Run(() => cancellation.CancelAsync(), CancellationToken.None);
            person.FirstName = $"raced{i}";
            await cancel.WaitAsync(WaitTimeout);
            await racedWait.AsTask().WaitAsync(WaitTimeout);
            Assert.Equal(1, DrainImmediately(subscription));

            var nextWait = subscription.WaitToDequeueAsync(CancellationToken.None);
            person.FirstName = $"next{i}";

            // Assert
            Assert.True(await nextWait.AsTask().WaitAsync(WaitTimeout));
            Assert.Equal(1, DrainImmediately(subscription));
        }
    }

    [Fact]
    public async Task WhenACompletedWaitsTokenIsCancelledLater_ThenTheNextWaitIsNotEndedByIt()
    {
        // Arrange: the first wait is completed by a write, so its cancellation registration must have
        // been released; a later wait with another token is still pending when the first token cancels.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        using var firstConsumer = new CancellationTokenSource();
        var firstWait = subscription.WaitToDequeueAsync(firstConsumer.Token);
        person.FirstName = "first";
        Assert.True(await firstWait.AsTask().WaitAsync(WaitTimeout));
        Assert.True(subscription.TryDequeueImmediate(out _));
        var nextWait = subscription.WaitToDequeueAsync(CancellationToken.None);

        // Act
        await firstConsumer.CancelAsync();

        // Assert
        Assert.False(nextWait.IsCompleted);
        person.FirstName = "next";
        Assert.True(await nextWait.AsTask().WaitAsync(WaitTimeout));
    }

    [Fact]
    public async Task WhenAWaitIsStillPending_ThenStartingAnotherThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var pendingWait = subscription.WaitToDequeueAsync(CancellationToken.None);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(StartAnotherWait);
        person.FirstName = "John";
        Assert.True(await pendingWait.AsTask().WaitAsync(WaitTimeout));

        void StartAnotherWait() => _ = subscription.WaitToDequeueAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenDisposeRacesAWait_ThenTheWaitCompletesAndCompletionIsReported()
    {
        // Arrange
        const int iterations = 2_000;
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();

        for (var i = 0; i < iterations; i++)
        {
            var subscription = context.CreatePropertyChangeQueueSubscription();

            // Act
            var dispose = Task.Run(subscription.Dispose, CancellationToken.None);
            var wait = subscription.WaitToDequeueAsync(CancellationToken.None);

            // Assert: a lost wake-up would leave the wait pending past the timeout.
            await wait.AsTask().WaitAsync(WaitTimeout);
            await dispose.WaitAsync(WaitTimeout);
            Assert.False(await subscription.WaitToDequeueAsync(CancellationToken.None));
        }
    }

    private static int DrainImmediately(PropertyChangeQueueSubscription subscription)
    {
        var count = 0;
        while (subscription.TryDequeueImmediate(out _))
        {
            count++;
        }

        return count;
    }

    private static async Task ConsumeWithAwaitableWaitAsync(
        PropertyChangeQueueSubscription subscription, StrongBox<int> received, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (subscription.TryDequeueImmediate(out _))
            {
                Volatile.Write(ref received.Value, received.Value + 1);
            }
            else if (!await subscription.WaitToDequeueAsync(cancellationToken))
            {
                return;
            }
        }
    }

    private static void ConsumeWithBlockingWait(
        PropertyChangeQueueSubscription subscription, StrongBox<int> received, CancellationToken cancellationToken)
    {
        while (subscription.TryDequeue(out _, cancellationToken))
        {
            Volatile.Write(ref received.Value, received.Value + 1);
        }
    }
}
