using System.Reactive.Linq;

using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class SynchronousChangeObservableTests
{
    public SynchronousChangeObservableTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenObservableIsNotSubscribed_ThenNoSubscriptionIsInstalled()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        // Act
        _ = new PropertyReference(person, nameof(Person.FirstName)).GetSynchronousChangeObservable();

        // Assert
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenTwoObserversSubscribe_ThenEachInstallsItsOwnSubscriptionAndBothReceive()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var observable = new PropertyReference(person, nameof(Person.FirstName)).GetSynchronousChangeObservable();
        var first = new List<string?>();
        var second = new List<string?>();

        using var firstSubscription = observable.Subscribe(change => first.Add(change.GetNewValue<string?>()));
        using var secondSubscription = observable.Subscribe(change => second.Add(change.GetNewValue<string?>()));

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.Equal(["Rico"], first);
        Assert.Equal(["Rico"], second);
    }

    [Fact]
    public void WhenRxHandleIsDisposed_ThenTheUnderlyingSubscriptionIsRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var received = new List<string?>();
        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .GetSynchronousChangeObservable()
            .Subscribe(change => received.Add(change.GetNewValue<string?>()));

        // Act
        subscription.Dispose();
        person.FirstName = "Rico";

        // Assert
        Assert.Empty(received);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenHandlerThrows_ThenItPropagatesToTheWriterAndTheSubscriptionStaysLive()
    {
        // Arrange: layer 1 inherits layer 0's contract rather than softening it. A throw reaching the
        // writer, and a subscription that survives it, is what pins the decision not to derive from
        // ObservableBase<T>, whose AutoDetachObserver would dispose on throw.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .GetSynchronousChangeObservable()
            .Subscribe(_ =>
            {
                deliveries++;
                throw new InvalidOperationException("handler failed");
            });

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => person.FirstName = "one");
        Assert.Throws<InvalidOperationException>(() => person.FirstName = "two");
        Assert.Equal(2, deliveries);
        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenComposedWithTakeOne_ThenTheUnderlyingSubscriptionIsReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var received = new List<string?>();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .GetSynchronousChangeObservable()
            .Take(1)
            .Subscribe(change => received.Add(change.GetNewValue<string?>()));

        // Act
        person.FirstName = "one";
        person.FirstName = "two";

        // Assert
        Assert.Equal(["one"], received);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenObserverIsNull_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var observable = new PropertyReference(person, nameof(Person.FirstName)).GetSynchronousChangeObservable();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => observable.Subscribe(null!));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenCalledTwice_ThenEachCallReturnsADistinctInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var firstReceived = new List<string?>();
        var secondReceived = new List<string?>();

        // Act
        var first = property.GetSynchronousChangeObservable();
        var second = property.GetSynchronousChangeObservable();

        using var firstSubscription = first.Subscribe(change => firstReceived.Add(change.GetNewValue<string?>()));
        using var secondSubscription = second.Subscribe(change => secondReceived.Add(change.GetNewValue<string?>()));

        person.FirstName = "Rico";

        // Assert: nothing may key observables by identity, and each instance carries its own subscription.
        Assert.NotSame(first, second);
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.Equal(["Rico"], firstReceived);
        Assert.Equal(["Rico"], secondReceived);
    }
}
