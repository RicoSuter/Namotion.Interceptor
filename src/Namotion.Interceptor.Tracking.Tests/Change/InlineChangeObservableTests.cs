using System.Reactive.Linq;

using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class InlineChangeObservableTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    public InlineChangeObservableTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenObservableIsNotSubscribed_ThenNoSubscriptionIsInstalled()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        // Act
        _ = new PropertyReference(person, nameof(Person.FirstName)).GetInlineChangeObservable();

        // Assert
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenTwoObserversSubscribe_ThenEachInstallsItsOwnSubscriptionAndBothReceive()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var observable = new PropertyReference(person, nameof(Person.FirstName)).GetInlineChangeObservable();
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
            .GetInlineChangeObservable()
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
            .GetInlineChangeObservable()
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
            .GetInlineChangeObservable()
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
    public void WhenComposedHandlerThrows_ThenItPropagatesToTheWriterAndTearsTheSubscriptionDown()
    {
        // Arrange: an operator wraps the handler in Rx's AutoDetachObserver, which disposes in a finally and
        // then rethrows, so both halves happen. Take(5) cannot complete on its own within this test, leaving
        // the throw as the only thing that can end the subscription.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .GetInlineChangeObservable()
            .Take(5)
            .Subscribe(_ =>
            {
                deliveries++;
                throw new InvalidOperationException("handler failed");
            });

        // Act & Assert: the exception reaches the writer,
        Assert.Throws<InvalidOperationException>(() => person.FirstName = "one");

        // and the subscription is gone, so a later write is neither delivered nor observable as a failure.
        person.FirstName = "two";
        Assert.Equal(1, deliveries);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenObserverIsNull_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var observable = new PropertyReference(person, nameof(Person.FirstName)).GetInlineChangeObservable();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => observable.Subscribe(null!));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenCalledTwice_ThenEachCallReturnsAnIndependentInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var firstReceived = new List<string?>();
        var secondReceived = new List<string?>();

        var first = property.GetInlineChangeObservable();
        var second = property.GetInlineChangeObservable();

        using var firstSubscription = first.Subscribe(change => firstReceived.Add(change.GetNewValue<string?>()));
        using var secondSubscription = second.Subscribe(change => secondReceived.Add(change.GetNewValue<string?>()));

        // Act
        person.FirstName = "Rico";

        // Assert: distinct instances are not enough on their own. A facade multicasting over one shared
        // subscription would pass NotSame and fill both lists, and is caught only by the count.
        Assert.NotSame(first, second);
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.Equal(["Rico"], firstReceived);
        Assert.Equal(["Rico"], secondReceived);
    }

    [Fact]
    public void WhenSeveralThreadsWriteTheProperty_ThenOneSubscriberIsNeverEnteredConcurrently()
    {
        // Arrange
        const int writerCount = 4;
        const int writesPerWriter = 500;

        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var observer = new OverlapProbeObserver();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .GetInlineChangeObservable()
            .Subscribe(observer);

        using var startGate = new ManualResetEventSlim();
        var writers = Enumerable
            .Range(0, writerCount)
            .Select(writerIndex => new Thread(() =>
            {
                startGate.Wait();
                for (var write = 0; write < writesPerWriter; write++)
                {
                    // Values are unique per writer so no write can be collapsed away before dispatch.
                    person.FirstName = $"{writerIndex}-{write}";
                }
            }) { IsBackground = true })
            .ToList();

        foreach (var writer in writers)
        {
            writer.Start();
        }

        // Act
        startGate.Set();

        // Assert
        foreach (var writer in writers)
        {
            Assert.True(writer.Join(WaitTimeout), "every writer thread should have finished");
        }

        Assert.Equal(0, observer.OverlapCount);
        Assert.Equal(writerCount * writesPerWriter, observer.DeliveryCount);
    }

    private sealed class OverlapProbeObserver : IObserver<SubjectPropertyChange>
    {
        private int _inFlight;
        private int _overlapCount;
        private int _deliveryCount;

        public int OverlapCount => Volatile.Read(ref _overlapCount);

        public int DeliveryCount => Volatile.Read(ref _deliveryCount);

        public void OnNext(SubjectPropertyChange value)
        {
            if (Interlocked.Increment(ref _inFlight) > 1)
            {
                Interlocked.Increment(ref _overlapCount);
            }

            // An overlap is only visible while a delivery sits inside OnNext, so the window is widened by
            // spinning rather than waiting: an unserialized adapter then overlaps within a few writes.
            Thread.SpinWait(200);

            Interlocked.Increment(ref _deliveryCount);
            Interlocked.Decrement(ref _inFlight);
        }

        public void OnError(Exception error) => Assert.Fail($"OnError is never raised: {error}");

        public void OnCompleted() => Assert.Fail("the sequence never completes");
    }
}
