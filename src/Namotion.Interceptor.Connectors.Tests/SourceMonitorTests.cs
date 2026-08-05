using System.Collections.Concurrent;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceMonitorTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithLifecycle()
            .WithSourceMonitoring();

    [Fact]
    public async Task WhenASourceRegisters_ThenSubscribersReceiveSourceRegistered()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));
        var source = new TestStateSource(new Person(context));

        // Act
        monitor.Register(source);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceRegistered));
        Assert.Contains(source, monitor.Sources);
    }

    [Fact]
    public async Task WhenARegisteredSourceTransitions_ThenTheMonitorForwardsStateChanged()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));
        monitor.Register(source);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        source.ReportSynchronized();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() =>
            received.Any(e => e.Kind == SourceEventKind.StateChanged && e.NewState == SourceState.Synchronized));
    }

    [Fact]
    public async Task WhenASourceUnregisters_ThenItsLaterTransitionsAreNotForwarded()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));
        monitor.Register(source);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        monitor.Unregister(source);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceUnregistered));
        source.ReportSynchronized();

        // Assert
        Assert.DoesNotContain(received, e => e.Kind == SourceEventKind.StateChanged);
        Assert.DoesNotContain(source, monitor.Sources);
    }

    [Fact]
    public void WhenRegisteringTwice_ThenTheSecondRegistrationEmitsNothing()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        monitor.Register(source);
        monitor.Register(source);

        // Assert
        Assert.Single(monitor.Sources);
    }

    [Fact]
    public void WhenUnregisteringAnUnknownSource_ThenNothingHappens()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));

        // Act & Assert
        monitor.Unregister(source);
        Assert.Empty(monitor.Sources);
    }

    [Fact]
    public async Task WhenOneHandlerThrows_ThenOtherSubscribersStillReceiveEvents()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var throwing = monitor.Subscribe(_ => throw new InvalidOperationException("subscriber is buggy"));
        using var healthy = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        monitor.Register(new TestStateSource(new Person(context)));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceRegistered));
    }

    [Fact]
    public async Task WhenOneHandlerIsSlow_ThenAnotherSubscriberIsNotDelayed()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var release = new ManualResetEventSlim(false);
        var fastReceived = new ManualResetEventSlim(false);
        using var slow = monitor.Subscribe(_ => release.Wait(TimeSpan.FromSeconds(30)));
        using var fast = monitor.Subscribe(_ => fastReceived.Set());

        // Act
        monitor.Register(new TestStateSource(new Person(context)));

        // Assert
        Assert.True(fastReceived.Wait(TimeSpan.FromSeconds(10)));
        release.Set();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WhenSubscribing_ThenTheSnapshotPlusDeliveredEventsSeeEachChangeOnce()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var before = new TestStateSource(new Person(context));
        monitor.Register(before);
        var received = new ConcurrentQueue<SourceEvent>();

        // Act
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));
        var after = new TestStateSource(new Person(context));
        monitor.Register(after);

        // Assert
        Assert.Contains(before, subscription.Sources);
        Assert.DoesNotContain(after, subscription.Sources);
        await AsyncTestHelpers.WaitUntilAsync(() =>
            received.Any(e => e.Kind == SourceEventKind.SourceRegistered && ReferenceEquals(e.Source, after)));
        Assert.DoesNotContain(received, e => ReferenceEquals(e.Source, before));
    }

    [Fact]
    public void WhenNoMonitorIsConfigured_ThenGetSourceMonitorThrowsWithGuidance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => context.GetSourceMonitor());
        Assert.Contains("WithSourceMonitoring", exception.Message);
    }

    [Fact]
    public void WhenTwoMonitorsAreReachable_ThenGetSourceMonitorThrows()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle().WithSourceMonitoring();
        var child = InterceptorSubjectContext.Create().WithSourceMonitoring();
        child.AddFallbackContext(parent);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => child.GetSourceMonitor());
        Assert.Contains("GetServices", exception.Message);
    }

    [Fact]
    public async Task WhenAStoppedSourceIsStartedAgain_ThenThePumpDoesNotRun()
    {
        // Arrange
        var context = CreateContext();
        var source = new TestStateSource(new Person(context));
        await source.StartAsync(CancellationToken.None);
        await source.StopAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.State == SourceState.Stopped);

        // Act
        await source.StartAsync(CancellationToken.None);

        // Assert
        // BackgroundService.StartAsync builds a FRESH linked CancellationTokenSource on every call
        // and StopAsync cancels only the previous one, so without the guard ExecuteAsync would run
        // a second time against an uncancelled token while State stayed pinned at Stopped.
        Assert.Equal(SourceState.Stopped, source.State);
        Assert.Equal(1, source.ExecuteCount);
    }

    [Fact]
    public void WhenASubjectIsAttachedToASecondTree_ThenOnlyTheFirstTreesMonitorIsReachable()
    {
        // Arrange
        var firstTree = CreateContext();
        var secondTree = CreateContext();
        var firstRoot = new Person(firstTree);
        var secondRoot = new Person(secondTree);
        var shared = new Person();
        firstRoot.Mother = shared;

        // Act
        secondRoot.Mother = shared;

        // Assert
        // Characterization, not aspiration: ContextInheritanceHandler adds a parent fallback only on
        // the FIRST attach ({ ReferenceCount: 1, IsContextAttach: true }), so the second tree's
        // monitor never becomes reachable and this design claims no multi-tree coverage. If context
        // inheritance ever starts tracking every parent, this test fails and the limitation, the
        // topology-aware CurrentState, and the docs all need revisiting together.
        var reachable = ((IInterceptorSubject)shared).Context.GetServices<SourceMonitor>();
        Assert.Single(reachable);
        Assert.Same(firstTree.GetSourceMonitor(), reachable[0]);
    }

    [Fact]
    public async Task WhenNoSubjectEverAttaches_ThenTheMonitorStillWorks()
    {
        // Arrange
        // Note: a monitor-bearing context ALWAYS has the lifecycle interceptor, because
        // WithSourceMonitoring implies WithParents and WithParents returns WithLifecycle. So the
        // reachable robustness case is a tree where nothing attaches, not one with no interceptor.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithSourceMonitoring();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        monitor.Register(new TestStateSource(new Person(context)));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceRegistered));
    }
}
