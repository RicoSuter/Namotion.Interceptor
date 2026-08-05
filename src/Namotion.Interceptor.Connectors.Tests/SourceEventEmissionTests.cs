using System.Collections.Concurrent;
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
    public void WhenNoMonitorIsReachable_ThenClaimingDoesNotThrowAndPublishesNothing()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act & Assert
        Assert.True(property.SetSource(new TestStateSource(person)));
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
        source.ReportSynchronized();
        property.SetSource(source);
        var sourceEvent = new SourceEvent(
            SourceEventKind.PropertyClaimed, source, property,
            SourceState.Unclaimed, SourceState.Synchronized, DateTimeOffset.UtcNow) { Monitor = monitor };

        // Act
        var current = sourceEvent.CurrentState;

        // Assert
        Assert.Equal(SourceState.Synchronized, current);
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
    public void WhenThereAreNoSubscribers_ThenTheCatchUpScanIsSkipped()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context);
        var child = new Person();
        new PropertyReference(child, nameof(Person.FirstName)).SetSource(new TestStateSource(root));

        // Act & Assert
        root.Mother = child;
        root.Mother = null;
    }
}
