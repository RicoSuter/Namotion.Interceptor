using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PerPropertySubscriptionLifecycleTests
{
    public PerPropertySubscriptionLifecycleTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenSameObserverSubscribedToMultipleProperties_ThenEachFiresIndependently()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var firstNameHits = 0;
        var lastNameHits = 0;
        using var a = new PropertyReference(person, nameof(Person.FirstName)).Subscribe((in SubjectPropertyChange _) => firstNameHits++);
        using var b = new PropertyReference(person, nameof(Person.LastName)).Subscribe((in SubjectPropertyChange _) => lastNameHits++);

        // Act
        person.FirstName = "John";
        person.LastName = "Doe";

        // Assert: each property's listener fires exactly once, so a double-fire on one and zero on the
        // other cannot pass (a single shared counter would).
        Assert.Equal(1, firstNameHits);
        Assert.Equal(1, lastNameHits);
    }

    [Fact]
    public void WhenReentrantWriteInCallback_ThenDeliversPerWriteWithoutDeadlock()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var lastNameHits = 0;
        using var first = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => person.LastName = "Doe");
        using var last = new PropertyReference(person, nameof(Person.LastName))
            .Subscribe((in SubjectPropertyChange _) => lastNameHits++);

        // Act
        person.FirstName = "John";

        // Assert
        Assert.Equal("Doe", person.LastName);
        Assert.Equal(1, lastNameHits);
    }

    [Fact]
    public void WhenRepeatedDispose_ThenNoOpAndCountNeverNegative()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var s = new PropertyReference(person, nameof(Person.FirstName)).Subscribe((in SubjectPropertyChange _) => { });

        // Subscribe raised the live count, so a later "returns to zero" is distinguishable from
        // "never left zero".
        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());

        // Act
        s.Dispose();
        s.Dispose();
        s.Dispose();

        // Assert
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenSubjectDetachedThenReattached_ThenSubscriptionRevives()
    {
        // Arrange: subscribe before attach. Person.Mother is a reference property, so assigning it
        // attaches/detaches the child via context inheritance (WithFullPropertyTracking).
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Person(context);
        var child = new Person(); // no context yet -> dormant
        var hits = 0;
        using var s = new PropertyReference(child, nameof(Person.FirstName)).Subscribe((in SubjectPropertyChange _) => hits++);

        // Act 1: dormant (not attached) -> no delivery
        child.FirstName = "A";
        Assert.Equal(0, hits);

        // Act 2: attach -> delivery resumes
        root.Mother = child;
        child.FirstName = "B";
        Assert.Equal(1, hits);

        // Act 3: detach -> dormant again
        root.Mother = null;
        child.FirstName = "C";
        Assert.Equal(1, hits);
    }

    [Fact]
    public void WhenTwoAggregatedInterceptors_ThenListenerDeliversExactlyOnce()
    {
        // Arrange: a subject whose context aggregates two full-tracking contexts.
        var parent = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var childCtx = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        childCtx.AddFallbackContext(parent);
        var person = new Person(childCtx);
        var hits = 0;
        using var s = new PropertyReference(person, nameof(Person.FirstName)).Subscribe((in SubjectPropertyChange _) => hits++);

        // Act
        person.FirstName = "John";

        // Assert (dedup flag guarantees once, not twice)
        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task WhenWatchedPropertyWrittenInTransaction_ThenListenerSilentDuringCaptureAndDeliversOnceAtCommit()
    {
        // Arrange: a single full-tracking, transaction-enabled context. This is the simplest case pinning
        // the target-side [RunsAfter] ordering (PropertyChangeInterceptor after SubjectTransactionInterceptor):
        // staged writes stay silent during capture and replay once at commit. The aggregated counterpart is
        // WhenAggregatedWithSingleTransactionContext...; only the symmetric aggregation (both contexts
        // .WithTransactions()) is inexpressible, because capture's TryGetService<SubjectTransactionInterceptor>
        // throws when two aggregated contexts each contribute one.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var person = new Person(context);
        var hits = 0;
        using var s = new PropertyReference(person, nameof(Person.FirstName)).Subscribe((in SubjectPropertyChange _) => hits++);

        // Act & Assert
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            // Staged during capture: the write never reaches the listener until commit replay.
            Assert.Equal(0, hits);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Delivered exactly once at commit replay.
        Assert.Equal(1, hits);
        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public async Task WhenAggregatedWithSingleTransactionContext_ThenStagedWritesStaySilentAndDeliverOnceAtCommit()
    {
        // Arrange: an ASYMMETRIC aggregation. A symmetric one (both contexts .WithTransactions()) cannot be
        // expressed because capture resolves the interceptor via TryGetService, which throws when two
        // aggregated contexts each contribute a SubjectTransactionInterceptor. Here only the parent fallback
        // adds transactions, so SubjectTransactionInterceptor resolves to exactly one instance while
        // PropertyChangeInterceptor aggregates to two (one per context). This pins the target-side
        // [RunsAfter] ordering across aggregated change interceptors: neither aggregated instance leaks a
        // staged write to a per-property listener during capture, and commit replay delivers exactly once.
        var child = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var parent = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        child.AddFallbackContext(parent);
        var person = new Person(child);
        var hits = 0;
        using var s = new PropertyReference(person, nameof(Person.FirstName)).Subscribe((in SubjectPropertyChange _) => hits++);

        // Act & Assert
        using (var transaction = await child.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            // Staged during capture: neither aggregated PropertyChangeInterceptor reaches the listener.
            Assert.Equal(0, hits);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Delivered exactly once at commit replay despite two aggregated change interceptors.
        Assert.Equal(1, hits);
        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public void WhenFirstObserverThrows_ThenLaterObserversAreSkippedAndQueueAlreadyDelivered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        using var queue = context.CreatePropertyChangeQueueSubscription();
        using var throwing = property.Subscribe((in SubjectPropertyChange _) => throw new InvalidOperationException("boom"));
        var laterHits = 0;
        using var later = property.Subscribe((in SubjectPropertyChange _) => laterHits++);

        // Act & Assert: dispatch is fail-fast, so the observer installed after the throwing one
        // is silently skipped; the queue is served before listeners, so it already has the change.
        Assert.Throws<InvalidOperationException>(() => person.FirstName = "John");
        Assert.Equal(0, laterHits);
        Assert.True(queue.TryDequeue(out var change, new CancellationTokenSource(1000).Token));
        Assert.Equal("John", change.GetNewValue<string?>());
    }

    [Fact]
    public void WhenDerivedDependencyChangesUnderFullTracking_ThenDerivedListenerFires()
    {
        // Arrange: full tracking includes derived-change detection. FullName = "{FirstName} {LastName}".
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        string? captured = null;
        using var s = new PropertyReference(person, nameof(Person.FullName))
            .Subscribe((in SubjectPropertyChange c) => captured = c.GetNewValue<string?>());

        // Act: writing a dependency recalculates FullName, which re-enters the write chain as its own
        // (fresh, unclaimed) write context.
        person.FirstName = "John";

        // Assert: the listener on the derived property fires with the recalculated value.
        Assert.Equal("John", captured);
    }

    [Fact]
    public void WhenDerivedDependencyChangesWithoutDerivedDetection_ThenDerivedListenerIsInert()
    {
        // Arrange: a bare notifications context has no DerivedPropertyChangeHandler.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var hits = 0;
        using var s = new PropertyReference(person, nameof(Person.FullName))
            .Subscribe((in SubjectPropertyChange _) => hits++);

        // Act: the dependency write never re-enters to recalc FullName, and manual RecalculateDerivedProperty
        // is also a no-op without the attached DerivedPropertyData.
        person.FirstName = "John";
        new PropertyReference(person, nameof(Person.FullName)).RecalculateDerivedProperty();

        // Assert: fully inert on a bare notifications context.
        Assert.Equal(0, hits);
    }

    [Fact]
    public void WhenTwoListenersOnSameProperty_ThenBothFireOnOneWrite()
    {
        // Arrange: two independent subscriptions on the SAME property.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var firstHits = 0;
        var secondHits = 0;
        var property = new PropertyReference(person, nameof(Person.FirstName));
        using var first = property.Subscribe((in SubjectPropertyChange _) => firstHits++);
        using var second = property.Subscribe((in SubjectPropertyChange _) => secondHits++);

        // Act
        person.FirstName = "John";

        // Assert: a single write fans out to both listeners on the property, each exactly once.
        Assert.Equal(1, firstHits);
        Assert.Equal(1, secondHits);
    }

    [Fact]
    public void WhenSubscriptionHandleDroppedWithoutDispose_ThenLiveCountStaysPositive()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        // Act: create a subscription and drop the returned handle without ever disposing it.
        _ = new PropertyReference(person, nameof(Person.FirstName)).Subscribe((in SubjectPropertyChange _) => { });

        // Assert: only Dispose decrements the process-wide live count, so a dropped, undisposed
        // subscription leaves it positive. No GC assertion is made.
        Assert.True(PropertyChangeSubscriptions.ReadSubscriptionCount() > 0);
    }

    [Fact]
    public async Task WhenConcurrentWritesRaceSubscriptionDispose_ThenNoExceptionEscapes()
    {
        // Arrange: one listener whose observer is captured into a dispatch local via Volatile.Read
        // before invocation, so a concurrent Dispose that null-clears it must be null-safe.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { });
        using var start = new ManualResetEventSlim(false);

        // Act & Assert: writers fan out and race the dispatch against a concurrent Dispose. Parallel.For
        // joins every writer and Task.WhenAll rethrows any faulted iteration, so an ObjectDisposedException
        // or NullReferenceException from the dispatch path would surface here and fail the test.
        var writers = Task.Run(() => Parallel.For(0, 2000, _ =>
        {
            start.Wait();
            person.FirstName = "John";
        }));
        var disposer = Task.Run(() =>
        {
            start.Wait();
            subscription.Dispose();
        });

        start.Set();
        await Task.WhenAll(writers, disposer);
    }

    [Fact]
    public async Task WhenSubscribeDisposeAndWriteRaceOnSameProperty_ThenLiveSubscriptionsSurvive()
    {
        // Arrange: four permanent listeners on one property that are never disposed during the storm;
        // the copy-on-write CAS install/remove must not drop any of them under concurrent churn.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var permanentHits = new int[4];
        var permanent = new IDisposable[permanentHits.Length];
        for (var i = 0; i < permanent.Length; i++)
        {
            var index = i;
            permanent[i] = property.Subscribe((in SubjectPropertyChange _) => Interlocked.Increment(ref permanentHits[index]));
        }

        using var start = new ManualResetEventSlim(false);

        // Act: concurrently churn short-lived subscriptions and write, all on the same property.
        var churn = Task.Run(() => Parallel.For(0, 500, _ =>
        {
            start.Wait();
            using var churned = property.Subscribe((in SubjectPropertyChange _) => { });
            person.FirstName = "x";
        }));
        var writers = Task.Run(() => Parallel.For(0, 500, _ =>
        {
            start.Wait();
            person.FirstName = "y";
        }));

        start.Set();
        await Task.WhenAll(churn, writers);

        // Assert: no permanent registration was lost by a racing CAS. After the storm has joined, one
        // quiescent write reaches every still-live listener exactly once.
        Array.Clear(permanentHits);
        person.FirstName = "final";
        Assert.All(permanentHits, hits => Assert.Equal(1, hits));

        foreach (var handle in permanent)
        {
            handle.Dispose();
        }
    }

    [Fact]
    public void WhenInnerInterceptorVetoesWrite_ThenNoNotificationIsPublished()
    {
        // Arrange: the veto interceptor sits deeper than PropertyChangeInterceptor and returns
        // without calling next, so the terminal never runs and IsWritten stays false.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        context.WithService(() => new VetoWriteInterceptor());
        var person = new Person(context);
        var queue = context.CreatePropertyChangeQueueSubscription();
        var hits = 0;
        using var listener = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => hits++);

        // Act
        person.FirstName = "John";

        // Assert: nothing was stored and nothing was published. Disposing first makes TryDequeue
        // non-blocking: it would drain a wrongly delivered item and returns false on an empty queue.
        queue.Dispose();
        Assert.Null(person.FirstName);
        Assert.Equal(0, hits);
        Assert.False(queue.TryDequeue(out _, CancellationToken.None));
    }

    [RunsAfter(typeof(PropertyChangeInterceptor))]
    private sealed class VetoWriteInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            // Intentionally does not call next: the write never commits.
        }
    }
}
