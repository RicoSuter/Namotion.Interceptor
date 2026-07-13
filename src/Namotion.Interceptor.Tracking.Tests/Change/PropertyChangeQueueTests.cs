using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class PropertyChangeQueueTests
{
    [Fact]
    public void WhenPropertyChanged_ThenChangeIsEnqueued()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeQueue();

        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var person = new Person(context);

        // Act
        person.FirstName = "John";

        // Assert
        Assert.True(subscription.TryDequeue(out var change, CancellationToken.None));
        Assert.Equal(nameof(Person.FirstName), change.Property.Name);
        Assert.Null(change.GetOldValue<string?>());
        Assert.Equal("John", change.GetNewValue<string?>());
    }

    [Fact]
    public void WhenMultiplePropertiesChanged_ThenAllChangesAreEnqueuedInOrder()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeQueue();

        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var person = new Person(context);

        // Act
        person.FirstName = "John";
        person.LastName = "Doe";

        // Assert
        Assert.True(subscription.TryDequeue(out var change1, CancellationToken.None));
        Assert.Equal(nameof(Person.FirstName), change1.Property.Name);

        Assert.True(subscription.TryDequeue(out var change2, CancellationToken.None));
        Assert.Equal(nameof(Person.LastName), change2.Property.Name);
    }

    [Fact]
    public void WhenMultipleSubscriptionsExist_ThenAllReceiveChanges()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeQueue();

        using var subscription1 = context.CreatePropertyChangeQueueSubscription();
        using var subscription2 = context.CreatePropertyChangeQueueSubscription();
        var person = new Person(context);

        // Act
        person.FirstName = "John";

        // Assert
        Assert.True(subscription1.TryDequeue(out var change1, CancellationToken.None));
        Assert.Equal("John", change1.GetNewValue<string?>());

        Assert.True(subscription2.TryDequeue(out var change2, CancellationToken.None));
        Assert.Equal("John", change2.GetNewValue<string?>());
    }

    [Fact]
    public void WhenSubscriptionDisposed_ThenTryDequeueReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeQueue();

        var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        subscription.Dispose();

        // Assert
        Assert.False(subscription.TryDequeue(out _, CancellationToken.None));
    }

    [Fact]
    public void WhenCancellationRequested_ThenTryDequeueReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeQueue();

        using var subscription = context.CreatePropertyChangeQueueSubscription();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = subscription.TryDequeue(out _, cts.Token);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task WhenNoChanges_ThenTryDequeueBlocksUntilChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeQueue();

        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var person = new Person(context);
        SubjectPropertyChange? received = null;

        // Act
        var consumer = Task.Run(() =>
        {
            if (subscription.TryDequeue(out var change, CancellationToken.None))
            {
                received = change;
            }
        });

        person.FirstName = "John";
        await AsyncTestHelpers.WaitUntilAsync(() => consumer.IsCompleted, message: "Consumer should have received change");

        // Assert
        Assert.NotNull(received);
        Assert.Equal("John", received.Value.GetNewValue<string?>());
    }

    [Fact]
    public async Task WhenSubscriptionDisposedWhileWaiting_ThenConsumerWakes()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeQueue();

        var subscription = context.CreatePropertyChangeQueueSubscription();
        bool? result = null;

        // Act
        var consumer = Task.Run(() =>
        {
            result = subscription.TryDequeue(out _, CancellationToken.None);
        });

        subscription.Dispose();
        await AsyncTestHelpers.WaitUntilAsync(() => consumer.IsCompleted, message: "Consumer should wake on dispose");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenUnsubscribed_ThenNoMoreChangesReceived()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeQueue();

        var subscription = context.CreatePropertyChangeQueueSubscription();
        var person = new Person(context);

        person.FirstName = "Before";
        Assert.True(subscription.TryDequeue(out _, CancellationToken.None));

        // Act
        subscription.Dispose();
        person.FirstName = "After";

        // Assert
        Assert.False(subscription.TryDequeue(out _, CancellationToken.None));
    }

    [Fact]
    public void WhenSetValueFromSourceCalled_ThenSourceAndTimestampsAreAvailable()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeQueue();

        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var person = new Person(context);
        var source = new object();
        var changedTimestamp = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);
        var receivedTimestamp = new DateTimeOffset(2026, 1, 15, 10, 30, 1, TimeSpan.Zero);

        // Act
        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetValueFromSource(source, changedTimestamp, receivedTimestamp, "FromSource");

        // Assert
        Assert.True(subscription.TryDequeue(out var change, CancellationToken.None));
        Assert.Equal("FromSource", change.GetNewValue<string?>());
        Assert.Same(source, change.Origin.Source);
        Assert.Equal(changedTimestamp, change.ChangedTimestamp);
        Assert.Equal(receivedTimestamp, change.ReceivedTimestamp);
    }

    [Fact]
    public void WhenQueueDisposed_ThenAllSubscriptionsDisposed()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeQueue();

        var queue = context.GetService<PropertyChangeQueue>();
        var subscription1 = queue.Subscribe();
        var subscription2 = queue.Subscribe();

        // Act
        queue.Dispose();

        // Assert
        Assert.False(subscription1.TryDequeue(out _, CancellationToken.None));
        Assert.False(subscription2.TryDequeue(out _, CancellationToken.None));
    }

    [Fact]
    public void WhenConcurrentProducers_ThenAllChangesAreDequeued()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeQueue();

        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var person = new Person(context);
        var changeCount = 100;

        // Act
        Parallel.For(0, changeCount, i =>
        {
            person.FirstName = $"Name{i}";
        });

        // Assert
        var dequeued = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (subscription.TryDequeue(out _, cts.Token))
        {
            dequeued++;
        }

        Assert.Equal(changeCount, dequeued);
    }
}
