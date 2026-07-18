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
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
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
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
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
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        var s = new PropertyReference(person, nameof(Person.FirstName)).Subscribe((in SubjectPropertyChange _) => { });

        // Subscribe raised the live count, so a later "returns to zero" is distinguishable from
        // "never left zero".
        Assert.Equal(1, PropertyChangeSubscriptions.ReadLiveCount());

        // Act
        s.Dispose();
        s.Dispose();
        s.Dispose();

        // Assert
        Assert.Equal(0, PropertyChangeSubscriptions.ReadLiveCount());
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
    public void WhenObserverThrows_ThenExceptionPropagates()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        using var s = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => throw new InvalidOperationException("boom"));

        // Act & Assert (must-not-throw contract: not caught by the framework)
        Assert.Throws<InvalidOperationException>(() => person.FirstName = "John");
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
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
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
}
