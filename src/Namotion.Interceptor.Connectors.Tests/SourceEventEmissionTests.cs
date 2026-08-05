using System.Collections.Concurrent;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceEventEmissionTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceMonitoring();

    [Fact]
    public async Task WhenAPropertyIsClaimed_ThenPropertyClaimedIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);

        // Act
        var claimed = property.SetSource(source);

        // Assert
        Assert.True(claimed);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyClaimed));
        var claimEvent = received.First(e => e.Kind == SourceEventKind.PropertyClaimed);
        Assert.Equal(SourceState.Unclaimed, claimEvent.OldState);
        Assert.Equal(SourceState.Connecting, claimEvent.NewState);
    }

    [Fact]
    public async Task WhenTheSameSourceReclaims_ThenNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        property.SetSource(source);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var claimed = property.SetSource(source);

        // Assert
        Assert.True(claimed);
        // Delivery is asynchronous, so an empty queue proves nothing on its own: a wrongly published
        // event may simply not have arrived. Publish a known event afterwards and wait for it; once
        // that has been delivered, anything enqueued before it would have been delivered too.
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Property?.Name == nameof(Person.LastName)));
        Assert.DoesNotContain(received, e => e.Property?.Name == nameof(Person.FirstName));
    }

    [Fact]
    public async Task WhenADifferentSourceClaimsAnOwnedProperty_ThenTheClaimIsRejectedAndNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetSource(new TestStateSource(person));
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var claimed = property.SetSource(new TestStateSource(person));

        // Assert
        Assert.False(claimed);
        // Same marker technique as above: an asynchronous absence needs a delivered successor.
        var marker = new TestStateSource(person);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(marker);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Property?.Name == nameof(Person.LastName)));
        Assert.DoesNotContain(received, e => e.Property?.Name == nameof(Person.FirstName));
    }

    [Fact]
    public async Task WhenOwnershipIsActuallyRemoved_ThenPropertyReleasedIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        property.SetSource(source);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var removed = property.RemoveSource(source);

        // Assert
        Assert.True(removed);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyReleased));
        var releaseEvent = received.First(e => e.Kind == SourceEventKind.PropertyReleased);
        Assert.Equal(SourceState.Unclaimed, releaseEvent.NewState);
    }

    [Fact]
    public async Task WhenRemovingAPropertyThatIsNotOwned_ThenNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var removed = property.RemoveSource(source);

        // Assert
        Assert.False(removed);
        // Delivery is asynchronous, so an empty queue proves nothing on its own: publish a known
        // event afterwards and wait for it, then anything wrongly published earlier would already
        // have been delivered too.
        var marker = new TestStateSource(person);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(marker);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Property?.Name == nameof(Person.LastName)));
        Assert.DoesNotContain(received, e => e.Kind == SourceEventKind.PropertyReleased);
    }

    [Fact]
    public async Task WhenRemovingAPropertyOwnedByADifferentSource_ThenNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var owningSource = new TestStateSource(person);
        property.SetSource(owningSource);
        var otherSource = new TestStateSource(person);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var removed = property.RemoveSource(otherSource);

        // Assert
        Assert.False(removed);
        Assert.True(property.TryGetSource(out var stillOwning));
        Assert.Same(owningSource, stillOwning);
        var marker = new TestStateSource(person);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(marker);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Property?.Name == nameof(Person.LastName)));
        Assert.DoesNotContain(received, e => e.Kind == SourceEventKind.PropertyReleased);
    }

    [Fact]
    public async Task WhenNoMonitorIsReachable_ThenClaimingDoesNotThrowAndPublishesNothing()
    {
        // Arrange
        // "No monitor reachable" itself has nothing to observe directly: PublishOwnershipChange's
        // early return on an empty monitor list is behaviourally identical to letting the (then
        // empty) foreach run, so no assertion here can distinguish the guard existing from it being
        // deleted - removing it changes nothing observable. What IS a real, checkable claim is that
        // this claim, made in a context with no monitor anywhere in its fallback chain, never
        // reaches an unrelated, independently monitored context - i.e. GetSourceMonitors() stays
        // correctly scoped to property.Subject.Context rather than leaking somewhere global.
        var isolatedContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var isolatedPerson = new Person(isolatedContext);
        var isolatedProperty = new PropertyReference(isolatedPerson, nameof(Person.FirstName));

        var monitoredContext = CreateContext();
        var monitor = monitoredContext.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var claimed = isolatedProperty.SetSource(new TestStateSource(isolatedPerson));

        // Assert
        Assert.True(claimed);
        var markerPerson = new Person(monitoredContext);
        new PropertyReference(markerPerson, nameof(Person.LastName)).SetSource(new TestStateSource(markerPerson));
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Property?.Name == nameof(Person.LastName)));
        Assert.DoesNotContain(received, e => ReferenceEquals(e.Property?.Subject, isolatedPerson));
    }

    [Fact]
    public void WhenTheSubjectIsStillAttached_ThenCurrentStateReadsThroughToTheSource()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        property.SetSource(source);
        // Captured while the source is still Connecting, so NewState here is Connecting - a
        // regression that made CurrentState just return NewState instead of re-resolving fresh
        // would still pass if the source never moved on after capture. It does, below.
        var sourceEvent = new SourceEvent(
            SourceEventKind.PropertyClaimed, source, property,
            SourceState.Unclaimed, SourceState.Connecting, DateTimeOffset.UtcNow) { Monitor = monitor };

        // Act - the source synchronizes after the event was captured.
        source.ReportSynchronized();
        var current = sourceEvent.CurrentState;

        // Assert
        Assert.Equal(SourceState.Synchronized, current);
        Assert.Equal(SourceState.Connecting, sourceEvent.NewState);
    }

    [Fact]
    public async Task WhenAClaimedSubjectAttaches_ThenPropertyEnteredViewIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var child = new Person();
        var property = new PropertyReference(child, nameof(Person.FirstName));
        property.SetSource(new TestStateSource(root));
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        root.Mother = child;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyEnteredView));
    }

    [Fact]
    public async Task WhenAStillClaimedSubjectDetaches_ThenPropertyLeftViewReportsUnclaimed()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var child = new Person();
        root.Mother = child;
        var property = new PropertyReference(child, nameof(Person.FirstName));
        property.SetSource(new TestStateSource(root));
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        root.Mother = null;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyLeftView));
        var leftEvent = received.First(e => e.Kind == SourceEventKind.PropertyLeftView);
        Assert.Equal(SourceState.Unclaimed, leftEvent.CurrentState);
        // Ownership is deliberately left intact so a re-attached subject still reaches its source.
        Assert.True(property.TryGetSource(out _));
    }

    [Fact]
    public async Task WhenASubjectConstructedDirectlyWithTheMonitoredContextDetaches_ThenPropertyLeftViewReportsUnclaimed()
    {
        // Arrange
        // The generator emits Context.AddFallbackContext(context) in this constructor overload, and
        // that fallback is never removed on detach. Because the child already exists in the monitor's
        // _attachedSubjects registry from that construction-time attach, the LATER property-based
        // attach below is not treated as this subject's first attach (IsContextAttach is false for
        // it), so ContextInheritanceHandler never adds root's context as a fallback either - the
        // construction-time fallback is the only reachability path to the monitor, and it is
        // permanent. A CurrentState resolved by asking whether the monitor is still reachable through
        // the subject's context would therefore keep reporting the source's state forever, even after
        // this subject has genuinely left the tree.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var child = new Person(context);
        root.Mother = child;
        var property = new PropertyReference(child, nameof(Person.FirstName));
        property.SetSource(new TestStateSource(root));
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        root.Mother = null;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyLeftView));
        var leftEvent = received.First(e => e.Kind == SourceEventKind.PropertyLeftView);
        Assert.Equal(SourceState.Unclaimed, leftEvent.CurrentState);
        Assert.True(property.TryGetSource(out _));
    }

    [Fact]
    public async Task WhenASubjectWithTwoParentsIsDetachedFromBoth_ThenPropertyLeftViewReportsUnclaimed()
    {
        // Arrange
        // ContextInheritanceHandler only adds the parent-context fallback on the subject's FIRST
        // attach (ReferenceCount: 1, IsContextAttach: true). The second parent's attach only
        // increments ReferenceCount and never adds its own fallback, so the removal that fires when
        // the second parent detaches (ReferenceCount: 0, IsPropertyReferenceRemoved: true) tries to
        // remove a fallback that was never added - a no-op - leaving the FIRST parent's fallback (and
        // therefore the monitor) permanently reachable through the subject's context, even though the
        // subject has genuinely left the tree via both parents.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var firstParent = new Person(context);
        var secondParent = new Person(context);
        var child = new Person();
        firstParent.Mother = child;
        secondParent.Mother = child;
        var property = new PropertyReference(child, nameof(Person.FirstName));
        property.SetSource(new TestStateSource(firstParent));
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        firstParent.Mother = null;
        secondParent.Mother = null;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyLeftView));
        var leftEvent = received.First(e => e.Kind == SourceEventKind.PropertyLeftView);
        Assert.Equal(SourceState.Unclaimed, leftEvent.CurrentState);
        Assert.True(property.TryGetSource(out _));
    }

    [Fact]
    public async Task WhenASubjectReattachesAfterDetaching_ThenCurrentStateReportsTheSourceAgain()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var child = new Person();
        root.Mother = child;
        var property = new PropertyReference(child, nameof(Person.FirstName));
        var source = new TestStateSource(root);
        property.SetSource(source);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        root.Mother = null;
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyLeftView));
        root.Mother = child;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyEnteredView));
        var enteredEvent = received.Last(e => e.Kind == SourceEventKind.PropertyEnteredView);
        Assert.Equal(SourceState.Connecting, enteredEvent.CurrentState);
        // Re-attach must restore membership without ever having touched ownership.
        Assert.True(property.TryGetSource(out var stillOwning));
        Assert.Same(source, stillOwning);
    }

    [Fact]
    public void WhenASubjectDetachesWithNoSubscribers_ThenTheMonitorNoLongerConsidersItAMember()
    {
        // Arrange
        // Membership tracking must not be gated behind HasSubscribers: CurrentState can be asked by
        // anyone at any time, not only by a subscriber processing an event. Proven directly through
        // the internal IsMember query rather than the event stream, since with no subscriber no event
        // is published at all to observe.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var child = new Person();
        root.Mother = child;
        Assert.False(monitor.HasSubscribers);
        Assert.True(monitor.IsMember(child));

        // Act
        root.Mother = null;

        // Assert
        Assert.False(monitor.IsMember(child));
    }

    [Fact]
    public void WhenThereAreNoSubscribers_ThenTheCatchUpScanIsSkipped()
    {
        // Arrange
        // Publish already no-ops on an empty subscription list, so merely asserting "no event was
        // received" would pass even if the HasSubscribers gate in HandleLifecycleChange were
        // deleted. Prove the scan itself never runs by observing property enumeration directly,
        // through a subject whose Properties getter counts its own accesses.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var subject = new PropertyEnumerationSpySubject();
        var attach = new SubjectLifecycleChange
        {
            Subject = subject,
            ReferenceCount = 1,
            IsContextAttach = true
        };
        var detach = new SubjectLifecycleChange
        {
            Subject = subject,
            ReferenceCount = 0,
            IsContextDetach = true
        };
        Assert.False(monitor.HasSubscribers);

        // Act
        monitor.HandleLifecycleChange(attach);
        monitor.HandleLifecycleChange(detach);

        // Assert
        Assert.Equal(0, subject.PropertiesAccessCount);
    }

    [Fact]
    public void WhenThereAreSubscribers_ThenTheCatchUpScanEnumeratesProperties()
    {
        // Arrange
        // Companion to the test above: proves the spy subject and the HasSubscribers gate actually
        // distinguish the two cases, rather than PropertiesAccessCount always staying at zero.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        using var subscription = monitor.Subscribe(_ => { });
        var subject = new PropertyEnumerationSpySubject();
        var attach = new SubjectLifecycleChange
        {
            Subject = subject,
            ReferenceCount = 1,
            IsContextAttach = true
        };
        Assert.True(monitor.HasSubscribers);

        // Act
        monitor.HandleLifecycleChange(attach);

        // Assert
        Assert.True(subject.PropertiesAccessCount > 0);
    }
}

/// <summary>
/// A hand-rolled (non-generated) subject whose Properties getter counts its own accesses, and
/// otherwise has no properties to enumerate. Used to prove ScanSubject's property-enumeration loop
/// runs or does not run, which an event-absence assertion alone cannot distinguish from Publish's
/// own no-op-on-empty-subscribers behaviour.
/// </summary>
internal sealed class PropertyEnumerationSpySubject : IInterceptorSubject
{
    private static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> EmptyProperties =
        new Dictionary<string, SubjectPropertyMetadata>();

    public int PropertiesAccessCount { get; private set; }

    public object SyncRoot { get; } = new();

    public IInterceptorSubjectContext Context { get; } = InterceptorSubjectContext.Create();

    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties
    {
        get
        {
            PropertiesAccessCount++;
            return EmptyProperties;
        }
    }

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
    {
    }
}
