using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class ScheduledPropertySubscriptionProtocolTests : IDisposable
{
    public ScheduledPropertySubscriptionProtocolTests()
    {
        PropertyChangeSubscriptions.ResetForTests();
        ScheduledPropertySubscription.EnableReentrancyInstrumentation = true;
    }

    public void Dispose() => ScheduledPropertySubscription.EnableReentrancyInstrumentation = false;

    [Fact]
    public void WhenChangesAreWritten_ThenOneDrainIsScheduledAndDeliversThemInOrder()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);

        // Act
        person.FirstName = "one";
        person.FirstName = "two";
        person.FirstName = "thre";

        // Assert: only the zero-to-one transition schedules, so three writes cost one work item.
        Assert.Equal(1, scheduler.ScheduleCallCount);
        Assert.Equal(3, subscription.PendingCount);

        scheduler.RunUntilIdle();
        Assert.Equal(["one", "two", "thre"], received);
    }

    [Fact]
    public void WhenTheQueueDrains_ThenTheCounterSettlesToZeroAndTheQueueIsEmpty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, scheduler);

        // Act
        for (var i = 0; i < 50; i++)
        {
            person.FirstName = i.ToString();
        }

        scheduler.RunUntilIdle();

        // Assert: this pairing is what pins the settle. The re-entrancy counter cannot substitute for it.
        Assert.Equal(0, subscription.WorkInProgressForTests);
        Assert.Equal(0, subscription.PendingCount);
    }

    [Fact]
    public void WhenMoreThanOneBatchIsQueued_ThenTheDrainYieldsAndHandsOffInsteadOfLooping()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var delivered = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => delivered++, scheduler);

        var total = ScheduledPropertySubscription.MaxBatch + 10;

        // Act
        for (var i = 0; i < total; i++)
        {
            person.FirstName = i.ToString();
        }

        var firstBatch = scheduler.RunOne();

        // Assert: the first work item stopped at the budget and queued a successor rather than
        // draining to empty, which is what keeps it from holding a scheduler thread.
        Assert.True(firstBatch);
        Assert.Equal(ScheduledPropertySubscription.MaxBatch, delivered);
        Assert.Equal(2, scheduler.ScheduleCallCount);

        scheduler.RunUntilIdle();
        Assert.Equal(total, delivered);
        Assert.Equal(0, subscription.WorkInProgressForTests);
    }

    [Fact]
    public void WhenAnObserverWritesTheSamePropertyDuringDelivery_ThenTheSuccessorIsDeliveredInTheSameBatch()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange change) =>
                {
                    received.Add(change.GetNewValue<string?>());
                    if (received.Count == 1)
                    {
                        // Written once only, so the drain terminates instead of feeding itself forever.
                        person.FirstName = "two";
                    }
                },
                scheduler);

        // Act
        person.FirstName = "one";
        scheduler.RunUntilIdle();

        // Assert: the write landed mid-batch, and the drain refreshed its snapshot rather than settling, so
        // it spent the remaining budget on it. A failure here means the refresh is gone and every change a
        // running batch accepts costs its own scheduler work item.
        Assert.Equal(["one", "two"], received);
        Assert.Equal(1, scheduler.ScheduleCallCount);
        Assert.Equal(0, subscription.WorkInProgressForTests);
        Assert.Equal(0, subscription.PendingCount);
    }

    [Fact]
    public async Task WhenManyWritersRaceOneProperty_ThenTheObserverIsNeverReenteredAndNothingIsLost()
    {
        // Arrange
        const int writers = 8;
        const int writesPerWriter = 2_000;

        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var delivered = 0;

        using var allDelivered = new CountdownEvent(writers * writesPerWriter);
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    delivered++; // deliberately unsynchronized: serialization is the contract under test
                    allDelivered.Signal();
                },
                System.Reactive.Concurrency.Scheduler.Default);

        // Act
        await Task.WhenAll(Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < writesPerWriter; i++)
            {
                person.FirstName = $"{writer}-{i}";
            }
        })));

        Assert.True(allDelivered.Wait(TimeSpan.FromSeconds(30)), $"only {delivered} of {writers * writesPerWriter} arrived");

        // Assert
        Assert.Equal(0, subscription.ReentrancyCountForTests);
        Assert.Equal(writers * writesPerWriter, delivered);
        Assert.Equal(0, subscription.PendingCount);
    }

    [Fact]
    public void WhenTheObserverThrows_ThenTheCounterStillSettlesAndDeliveryContinues()
    {
        // Arrange: an observer exception is caught, routed to onError and does not stop later deliveries.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var errors = new List<Exception>();
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    deliveries++;
                    if (deliveries == 1)
                    {
                        throw new InvalidOperationException("observer failed");
                    }
                },
                scheduler,
                errors.Add);

        // Act
        person.FirstName = "one";
        person.FirstName = "two";
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(2, deliveries);
        Assert.Single(errors);
        Assert.Equal(0, subscription.WorkInProgressForTests);
        Assert.Equal(0, subscription.PendingCount);
    }
}
