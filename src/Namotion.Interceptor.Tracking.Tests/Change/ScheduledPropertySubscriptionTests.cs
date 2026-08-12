using System.Linq.Expressions;
using System.Reactive.Concurrency;

using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class ScheduledPropertySubscriptionTests
{
    // Generous on purpose: a loaded machine must not fail these, and a real regression fails them by
    // never reaching the awaited state at all rather than by being slow.
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    public ScheduledPropertySubscriptionTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenSubscribedByTypedSelector_ThenChangesAreDeliveredOnTheScheduler()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = person.SubscribeToProperty(
            x => x.FirstName,
            (in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()),
            scheduler);

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Empty(received);
        scheduler.RunUntilIdle();
        Assert.Equal(["Rico"], received);
    }

    [Fact]
    public void WhenSelectorIsNotADirectPropertyAccess_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => person.SubscribeToProperty(
            x => x.Father!.FirstName,
            (in SubjectPropertyChange _) => { },
            scheduler));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenAnyScheduledSubscribeArgumentIsNull_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var scheduler = new ControllableScheduler();

        // Act & Assert: a bare null is ambiguous between the observer and callback overloads, so each
        // needs its own cast, exactly as the unscheduled guard test does.
        Assert.Throws<ArgumentNullException>(() => property.Subscribe((IPropertyChangeObserver)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => property.Subscribe((PropertyChangeCallback)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, null!));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty(x => x.FirstName, (IPropertyChangeObserver)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty(x => x.FirstName, (PropertyChangeCallback)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, null!));
        Assert.Throws<ArgumentNullException>(() => ((Person)null!).SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, scheduler));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty((Expression<Func<Person, string?>>)null!, (in SubjectPropertyChange _) => { }, scheduler));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenSchedulerIsSynchronous_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act & Assert: both spellings are reference-equal to the singletons. The typed overloads are
        // asserted too, because they reject only by delegating, and a refactor reaching for the internal
        // Create would drop that delegation without failing any other test.
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, ImmediateScheduler.Instance));
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, Scheduler.Immediate));
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, CurrentThreadScheduler.Instance));
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, Scheduler.CurrentThread));
        Assert.Throws<ArgumentException>(() => person.SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, ImmediateScheduler.Instance));
        Assert.Throws<ArgumentException>(() => person.SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, CurrentThreadScheduler.Instance));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenPropertyIsNotIntercepted_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            new PropertyReference(person, "NotAProperty").Subscribe((in SubjectPropertyChange _) => { }, scheduler));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenObserverThrows_ThenTheSetterReturnsNormally()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    deliveries++;
                    throw new InvalidOperationException("boom");
                },
                scheduler);

        // Act
        person.FirstName = "one";
        scheduler.RunUntilIdle();

        // Assert: the counter is what stops a drain that never delivers from passing this test.
        Assert.Equal(1, deliveries);
        Assert.Equal("one", person.FirstName);
    }

    [Fact]
    public void WhenScheduledObserverThrows_ThenAnUnscheduledListenerOnTheSamePropertyStillFires()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var scheduler = new ControllableScheduler();
        var unscheduled = new List<string?>();
        var scheduledDeliveries = 0;

        using var scheduled = property.Subscribe(
            (in SubjectPropertyChange _) =>
            {
                scheduledDeliveries++;
                throw new InvalidOperationException("boom");
            },
            scheduler);
        using var plain = property.Subscribe((in SubjectPropertyChange change) => unscheduled.Add(change.GetNewValue<string?>()));

        // Act
        person.FirstName = "one";
        scheduler.RunUntilIdle();

        // Assert: the counter pins that the throwing observer really ran, so a drain delivering nothing
        // cannot leave this green, and the failure cannot suppress another channel on the same write.
        Assert.Equal(1, scheduledDeliveries);
        Assert.Equal(["one"], unscheduled);
    }

    [Fact]
    public async Task WhenWriteCommitsAfterSubscribeReturns_ThenItIsDelivered()
    {
        // Arrange: the blocker parks the writer after PropertyChangeInterceptor's pre-commit work and
        // before the terminal commit, so the subscription installs mid-write.
        var blocker = new BlockingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        context.WithService(() => blocker);
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        var writer = Task.Run(() => person.FirstName = "John");
        Assert.True(blocker.EnteredInnerChain.Wait(TimeSpan.FromSeconds(10)));

        // Act
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);
        blocker.ProceedWithCommit.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(["John"], received);
    }

    [Fact]
    public void WhenSubjectIsDetachedWithChangesQueued_ThenThoseChangesAreStillDelivered()
    {
        // Arrange: dormancy stops acceptance, not the drain. This is the one place the scheduled path
        // does not inherit the unscheduled semantics, and it is the opposite of disposal.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var parent = new Person(context);
        var child = new Person();
        parent.Father = child;

        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = new PropertyReference(child, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);

        child.FirstName = "one";

        // Act
        parent.Father = null;
        child.FirstName = "after-detach";
        scheduler.RunUntilIdle();

        // Assert: both halves at once. The change accepted before the detach still drains, and the write
        // after it is not accepted, so the test cannot pass with a detach that does nothing.
        Assert.Equal(["one"], received);
    }

    [Fact]
    public void WhenSubjectIsDetachedAndReattached_ThenTheSubscriptionRevives()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var parent = new Person(context);
        var child = new Person();
        parent.Father = child;

        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = new PropertyReference(child, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);

        parent.Father = null;
        child.FirstName = "dorm";
        scheduler.RunUntilIdle();
        Assert.Empty(received);

        // Act
        parent.Father = child;
        child.FirstName = "live";
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(["live"], received);
    }

    [Fact]
    public void WhenOneObserverIsSharedAcrossTwoSubscriptions_ThenItCanBeInvokedConcurrently()
    {
        // Arrange: serialization is promised per subscription, not per observer instance, which is exactly
        // what the docs say, so an observer shared across two subscriptions has to synchronize itself. The
        // observer proves the promise by holding both deliveries inside OnChange at the same time, and both
        // subscriptions run on the real thread pool so the concurrency is genuine. The two properties are
        // different, so the subscriptions are independent of each other.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var observer = new OverlapProbeObserver();

        using var first = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(observer, Scheduler.Default);
        using var second = new PropertyReference(person, nameof(Person.LastName))
            .Subscribe(observer, Scheduler.Default);

        // Act
        person.FirstName = "one";
        person.LastName = "two";

        // Assert: a lock around delivery, process-wide or per observer, makes the overlap unobservable and
        // fails here.
        Assert.True(observer.WaitForAllDeliveries(WaitTimeout), "both deliveries should have completed");
        Assert.True(observer.OverlapObserved, "the two deliveries never overlapped");
    }

    [Fact]
    public void WhenManyPropertiesAreSubscribed_ThenNoThreadIsDedicatedPerSubscription()
    {
        // Arrange
        const int subscriptionCount = 100;

        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var people = Enumerable.Range(0, subscriptionCount).Select(_ => new Person(context)).ToList();
        var threadIds = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();

        using var allDelivered = new CountdownEvent(subscriptionCount);
        var subscriptions = people
            .Select(person => new PropertyReference(person, nameof(Person.FirstName))
                .Subscribe(
                    (in SubjectPropertyChange _) =>
                    {
                        threadIds.TryAdd(Environment.CurrentManagedThreadId, 0);
                        allDelivered.Signal();
                    },
                    Scheduler.Default))
            .ToList();

        try
        {
            // Act
            foreach (var person in people)
            {
                person.FirstName = "Rico";
            }

            Assert.True(allDelivered.Wait(TimeSpan.FromSeconds(30)));

            // Assert: the thread-per-subscription regression produces exactly subscriptionCount distinct
            // ids. This does not catch an unbounded drain, which occupies threads without adding ids.
            Assert.True(
                threadIds.Count < subscriptionCount / 2,
                $"{threadIds.Count} distinct delivery threads for {subscriptionCount} subscriptions");
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }
    }

    /// <summary>
    /// One instance shared by two subscriptions. Each delivery announces itself and then waits for the
    /// other one, so <see cref="OverlapObserved"/> is true only if both were inside
    /// <see cref="OnChange"/> at the same moment. Any lock serializing delivery makes that wait time out.
    /// </summary>
    private sealed class OverlapProbeObserver : IPropertyChangeObserver, IDisposable
    {
        private const int ExpectedDeliveries = 2;

        private readonly ManualResetEventSlim _bothArrived = new();
        private readonly CountdownEvent _allCompleted = new(ExpectedDeliveries);

        private int _inFlight;
        private int _overlapObserved;

        public bool OverlapObserved => Volatile.Read(ref _overlapObserved) == 1;

        public void OnChange(in SubjectPropertyChange change)
        {
            if (Interlocked.Increment(ref _inFlight) == ExpectedDeliveries)
            {
                // Reaching the expected count means the earlier delivery has not left OnChange yet, since
                // it decrements only below.
                Volatile.Write(ref _overlapObserved, 1);
                _bothArrived.Set();
            }
            else if (!_bothArrived.Wait(WaitTimeout))
            {
                // Serialized delivery lands here. Setting the gate keeps the next arrival from paying the
                // timeout a second time, so the failure is reported once rather than compounding.
                _bothArrived.Set();
            }

            Interlocked.Decrement(ref _inFlight);
            _allCompleted.Signal();
        }

        public bool WaitForAllDeliveries(TimeSpan timeout) => _allCompleted.Wait(timeout);

        public void Dispose()
        {
            _bothArrived.Dispose();
            _allCompleted.Dispose();
        }
    }
}
