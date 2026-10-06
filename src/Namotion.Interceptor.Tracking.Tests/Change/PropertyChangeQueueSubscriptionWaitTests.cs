using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class PropertyChangeQueueSubscriptionWaitTests
{
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
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(30)));
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
        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(30)));
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

        // Assert
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(30)));
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
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref received.Value) == i, TimeSpan.FromSeconds(10)))
            {
                break;
            }

            completedExchanges = i;
        }

        await stop.CancelAsync();
        await consumer.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal(exchangeCount, completedExchanges);
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
