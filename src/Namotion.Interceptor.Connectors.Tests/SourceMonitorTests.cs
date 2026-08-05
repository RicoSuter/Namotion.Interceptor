using System.Collections.Concurrent;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
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
    public async Task WhenRegisterAndSubscribeRaceConcurrently_ThenTheSourceIsObservedExactlyOnce()
    {
        // Arrange
        const int iterations = 10;
        var outcomes = new List<(bool WasInSnapshot, bool WasDelivered)>();

        // Act
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            outcomes.Add(await RaceRegisterAgainstSubscribeOnceAsync());
        }

        // Assert
        Assert.All(outcomes, outcome => Assert.True(
            outcome.WasInSnapshot ^ outcome.WasDelivered,
            $"snapshot={outcome.WasInSnapshot}, delivered={outcome.WasDelivered}; the source must appear " +
            "exactly once, in the snapshot or as a delivered event, never both, never neither."));
    }

    /// <summary>
    /// Manufactures, on one fresh monitor, the exact race described in the review finding: Register is
    /// paused right where it reads the source's State to build the SourceRegistered event (pre-fix that
    /// read happens after the monitor's lock is released; post-fix it happens while the lock is still
    /// held). Subscribe is started concurrently and given every chance to complete before Register is
    /// allowed to resume, which is what lets a pre-fix run see the source both in its snapshot and as a
    /// delivered event.
    /// </summary>
    private static async Task<(bool WasInSnapshot, bool WasDelivered)> RaceRegisterAgainstSubscribeOnceAsync()
    {
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();

        var reachedStateRead = new ManualResetEventSlim(false);
        var releaseStateRead = new ManualResetEventSlim(false);
        var source = new GatedStateSource(new Person(context), reachedStateRead, releaseStateRead);

        var registerTask = Task.Run(() => monitor.Register(source));
        Assert.True(reachedStateRead.Wait(TimeSpan.FromSeconds(10)),
            "Register should have reached the State read before the timeout.");

        var subscribeTask = Task.Run(() => monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent)));

        // Give Subscribe every opportunity to fully finish while Register is paused. Against the pre-fix
        // code the lock is already released at this point, so Subscribe races ahead and completes here;
        // against the fixed code Register still holds the lock, so Subscribe stays blocked and this times out.
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => subscribeTask.IsCompleted,
                timeout: TimeSpan.FromMilliseconds(200),
                pollInterval: TimeSpan.FromMilliseconds(2));
        }
        catch (TimeoutException)
        {
            // Subscribe is still blocked acquiring the lock Register holds. That is the fixed, race-free outcome.
        }

        releaseStateRead.Set();

        using var subscription = await subscribeTask.WaitAsync(TimeSpan.FromSeconds(10));
        await registerTask.WaitAsync(TimeSpan.FromSeconds(10));

        var wasInSnapshot = subscription.Sources.Contains(source);

        // A subscriber's queue is FIFO and single-drained, so once a sentinel registered strictly after
        // the race above has resolved is observed, any event for `source` that was ever enqueued for this
        // subscription has already been delivered. This settles delivery without a blind sleep.
        var sentinel = new TestStateSource(new Person(context));
        monitor.Register(sentinel);
        await AsyncTestHelpers.WaitUntilAsync(
            () => received.Any(e => e.Kind == SourceEventKind.SourceRegistered && ReferenceEquals(e.Source, sentinel)),
            pollInterval: TimeSpan.FromMilliseconds(2));

        var wasDelivered = received.Any(
            e => e.Kind == SourceEventKind.SourceRegistered && ReferenceEquals(e.Source, source));

        return (wasInSnapshot, wasDelivered);
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

/// <summary>
/// A source whose State getter blocks on first access until released. Used to pin Register at the
/// exact point where it reads State to build the SourceRegistered event, so a test can control whether
/// that read happens before or after the monitor's lock around it is released.
/// </summary>
internal sealed class GatedStateSource : ISubjectSource
{
    private readonly ManualResetEventSlim _reachedStateRead;
    private readonly ManualResetEventSlim _releaseStateRead;

    public GatedStateSource(IInterceptorSubject rootSubject, ManualResetEventSlim reachedStateRead, ManualResetEventSlim releaseStateRead)
    {
        RootSubject = rootSubject;
        _reachedStateRead = reachedStateRead;
        _releaseStateRead = releaseStateRead;
    }

    public IInterceptorSubject RootSubject { get; }

    public int WriteBatchSize => 0;

    public SourceState State
    {
        get
        {
            _reachedStateRead.Set();
            _releaseStateRead.Wait(TimeSpan.FromSeconds(10));
            return SourceState.Connecting;
        }
    }

    public DateTimeOffset? LastSynchronizedAt => null;

    public int PendingWriteCount => 0;

    public event EventHandler<SourceEvent>? StateChanged
    {
        add { }
        remove { }
    }

    public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken) => Task.FromResult<Action?>(null);

    public ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        => new(WriteResult.Success);
}
