using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

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

    [Fact]
    public void WhenDisposedBeforeTheQueueDrains_ThenQueuedChangesAreDroppedAndReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);

        person.FirstName = "one";
        person.FirstName = "two";
        Assert.Equal(2, subscription.PendingCount);

        // Act
        subscription.Dispose();
        scheduler.RunUntilIdle();

        // Assert: dropped, and released rather than retained behind the handle.
        Assert.Empty(received);
        Assert.Equal(0, subscription.PendingCount);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenDisposedTwice_ThenTheProcessWideCountIsDecrementedOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var other = new Person(context);
        var scheduler = new ControllableScheduler();

        using var keepAlive = new PropertyReference(other, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, scheduler);
        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, scheduler);

        // Act
        subscription.Dispose();
        subscription.Dispose();

        // Assert: a double decrement would zero the process-wide gate and silently stop per-property
        // delivery for every other live subscription in the host.
        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());

        // Releasing has to go through the upstream's own one-shot Dispose rather than decrementing the
        // gate directly: only the former also drops the subject-stored listener entry.
        Assert.False(new PropertyReference(person, nameof(Person.FirstName))
            .TryGetPropertyData(PropertyChangeSubscription.ListenersKey, out _));
    }

    [Fact]
    public void WhenTheSchedulerThrows_ThenItIsReportedAndDoesNotReachTheWriter()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var errors = new List<Exception>();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, new ThrowingScheduler(), errors.Add);

        // Act
        person.FirstName = "one";

        // Assert: the setter returned normally, and the fault released the subscription exactly once.
        Assert.Equal("one", person.FirstName);
        Assert.IsType<ObjectDisposedException>(Assert.Single(errors));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenTheSchedulerThrowsAndTheSubscriptionIsAlreadyDisposed_ThenTheCountDoesNotDrift()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var other = new Person(context);
        var scheduler = new ControllableScheduler();

        using var keepAlive = new PropertyReference(other, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, scheduler);

        var errors = new List<Exception>();
        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, new ThrowingScheduler(), errors.Add);

        // Act: dispose first, then provoke the fault path.
        subscription.Dispose();
        person.FirstName = "one";

        // Assert: acceptance stopped at dispose, so no fault fires and the gate is untouched.
        Assert.Empty(errors);
        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());

        // The dispose released the upstream itself, so the write above found no listener to forward to.
        Assert.False(new PropertyReference(person, nameof(Person.FirstName))
            .TryGetPropertyData(PropertyChangeSubscription.ListenersKey, out _));
    }

    [Fact]
    public void WhenTheSchedulerAcceptsWorkAndNeverRunsIt_ThenTheSubscriptionGoesQuietWithoutReporting()
    {
        // Arrange: pins the documented limit rather than a promise. There is no cheap liveness escape
        // that does not add a timer per subscription.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new BlackHoleScheduler();
        var errors = new List<Exception>();
        var delivered = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => delivered++, scheduler, errors.Add);

        // Act
        for (var i = 0; i < 20; i++)
        {
            person.FirstName = i.ToString();
        }

        // Assert
        Assert.Equal(0, delivered);
        Assert.Empty(errors);
        Assert.Equal(1, scheduler.ScheduleCallCount);
        Assert.Equal(20, subscription.PendingCount);
    }

    [Fact]
    public void WhenOnErrorThrows_ThenNothingEscapesIntoTheSchedulerAndDeliveryContinues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var inner = new ControllableScheduler();
        var scheduler = new RecordingScheduler(inner);
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    deliveries++;
                    throw new InvalidOperationException("observer failed");
                },
                scheduler,
                _ => throw new InvalidOperationException("error handler failed"));

        // Act
        person.FirstName = "one";
        person.FirstName = "two";
        inner.RunUntilIdle();

        // Assert
        Assert.Empty(scheduler.Escaped);
        Assert.Equal(2, deliveries);
        Assert.Equal(0, subscription.WorkInProgressForTests);
    }

    [Fact]
    public void WhenTheObserverThrowsWithNoErrorHandler_ThenItIsSwallowedAndDeliveryContinues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var inner = new ControllableScheduler();
        var scheduler = new RecordingScheduler(inner);
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    deliveries++;
                    throw new InvalidOperationException("observer failed");
                },
                scheduler);

        // Act
        person.FirstName = "one";
        person.FirstName = "two";
        inner.RunUntilIdle();

        // Assert
        Assert.Empty(scheduler.Escaped);
        Assert.Equal(2, deliveries);
    }

    [Fact]
    public void WhenTheWriterCarriesAmbientState_ThenTheObserverDoesNotSeeIt()
    {
        // Arrange: an unsuppressed ExecutionContext would let a delivery observe the writer's
        // SubjectTransaction.Current and mutate a pooled, already-returned dictionary.
        // This must run on a real scheduler. ControllableScheduler enqueues a bare closure and runs it
        // inline on the pump thread, so it captures no ExecutionContext and would pass with the
        // suppression removed, making the assertion vacuous.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var ambient = new AsyncLocal<string?>();
        string? observed = "not-run";

        using var delivered = new CountdownEvent(1);
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    observed = ambient.Value;
                    delivered.Signal();
                },
                System.Reactive.Concurrency.Scheduler.Default);

        // Act
        ambient.Value = "writer-scope";
        person.FirstName = "one";
        ambient.Value = null;

        // Assert
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(30)));
        Assert.Null(observed);
    }

    [Fact]
    public async Task WhenDeliveryHappensDuringATransactionCommit_ThenTheObserverDoesNotSeeTheTransaction()
    {
        // Arrange: the corruption this guards against is an observer writing a property under an
        // inherited transaction whose pending-change dictionary has already been returned to a pool.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var person = new Person(context);
        object? observedTransaction = new object();

        using var delivered = new CountdownEvent(1);
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    observedTransaction = SubjectTransaction.Current;
                    delivered.Signal();
                },
                System.Reactive.Concurrency.Scheduler.Default);

        // Act: the commit applies the captured change on the writer thread with the transaction still
        // ambient, so the drain is scheduled from inside that scope.
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "one";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(30)));
        Assert.Null(observedTransaction);
    }
}
