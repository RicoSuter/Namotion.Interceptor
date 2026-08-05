using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceWaitTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceMonitoring();

    [Fact]
    public void WhenTheMonitorIsCreated_ThenRegistrationIsIncomplete()
    {
        // Arrange & Act
        var monitor = CreateContext().GetSourceMonitor();

        // Assert
        Assert.False(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenRegistrationIsCompleted_ThenTheFlagFlipsAndIsIdempotent()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();

        // Act
        monitor.CompleteSourceRegistration();
        monitor.CompleteSourceRegistration();

        // Assert
        Assert.True(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenACompletionHoldIsTaken_ThenRegistrationIsIncompleteUntilItIsReleased()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();
        monitor.CompleteSourceRegistration();

        // Act
        var hold = monitor.DeferWaitCompletion();

        // Assert
        Assert.False(monitor.IsRegistrationComplete);
        hold.Dispose();
        Assert.True(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenHoldsAreNested_ThenRegistrationCompletesOnlyAfterTheLastRelease()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();
        monitor.CompleteSourceRegistration();

        // Act
        var outer = monitor.DeferWaitCompletion();
        var inner = monitor.DeferWaitCompletion();
        inner.Dispose();

        // Assert
        Assert.False(monitor.IsRegistrationComplete);
        outer.Dispose();
        Assert.True(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenTheContextExtensionIsUsed_ThenEveryReachableMonitorIsSignalled()
    {
        // Arrange
        var context = CreateContext();

        // Act
        context.CompleteSourceRegistration();

        // Assert
        Assert.True(context.GetSourceMonitor().IsRegistrationComplete);
    }

    [Fact]
    public void WhenTwoMonitorsAreReachable_ThenCompleteSourceRegistrationSignalsAll()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithLifecycle()
            .WithSourceMonitoring();
        var child = InterceptorSubjectContext.Create().WithSourceMonitoring();
        child.AddFallbackContext(parent);

        var parentMonitor = parent.GetSourceMonitor();
        var childMonitor = child.GetServices<SourceMonitor>()[0];

        // Act
        child.CompleteSourceRegistration();

        // Assert
        Assert.True(parentMonitor.IsRegistrationComplete);
        Assert.True(childMonitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenDeferWaitCompletionIsCalledWithTwoMonitors_ThenBothHoldsAreReleased()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithLifecycle()
            .WithSourceMonitoring();
        var child = InterceptorSubjectContext.Create().WithSourceMonitoring();
        child.AddFallbackContext(parent);

        var parentMonitor = parent.GetSourceMonitor();
        var childMonitor = child.GetServices<SourceMonitor>()[0];

        parentMonitor.CompleteSourceRegistration();
        childMonitor.CompleteSourceRegistration();

        // Act
        var hold = child.DeferWaitCompletion();

        // Assert - both should be incomplete while holds are out
        Assert.False(parentMonitor.IsRegistrationComplete);
        Assert.False(childMonitor.IsRegistrationComplete);

        // Act - release the holds
        hold.Dispose();

        // Assert - both should be complete
        Assert.True(parentMonitor.IsRegistrationComplete);
        Assert.True(childMonitor.IsRegistrationComplete);
    }

    [Fact]
    public async Task WhenRegistrationIsIncomplete_ThenTheWaitDoesNotComplete()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        source.ReportSynchronized();

        // Act
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.False(wait.IsCompleted);
        monitor.CompleteSourceRegistration();
        await wait;
    }

    [Fact]
    public async Task WhenAnInScopeSourceIsConnecting_ThenTheWaitBlocksUntilItSynchronizes()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();

        // Act
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);
        source.ReportSynchronized();

        // Assert
        await wait;
    }

    [Fact]
    public async Task WhenASiblingBranchSourceNeverSynchronizes_ThenAScopedWaitStillCompletes()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var left = new Person();
        var right = new Person();
        root.Mother = left;
        root.Father = right;
        var healthy = new TestStateSource(left);
        var broken = new TestStateSource(right);
        monitor.Register(healthy);
        monitor.Register(broken);
        monitor.CompleteSourceRegistration();
        healthy.ReportSynchronized();

        // Act
        await left.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(SourceState.Connecting, broken.State);
    }

    [Fact]
    public async Task WhenNoInScopeSourceIsRegistered_ThenTheWaitBlocks()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        monitor.CompleteSourceRegistration();

        // Act
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        await Task.Yield();
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public void WhenScopeBecomesEmptyAfterUnrelatedReEvaluations_ThenTheEmptyScopeWarningStillFires()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var recordingLogger = new RecordingLogger();
        context.AddService<ILoggerFactory>(new RecordingLoggerFactory(recordingLogger));
        using var subscription = monitor.Subscribe(_ => { }); // resolves the monitor's lazy logger

        var anchor = new Person(context);
        var source = new TestStateSource(anchor);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();

        // The source stays Connecting, so the wait is pending, but its scope is not empty: it is
        // matched, just not yet satisfied. This is what an unrelated re-evaluation must not confuse
        // with a genuinely empty scope.
        var wait = anchor.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        // Act - unrelated re-evaluations while the wait's own branch is still matched. Every one of
        // these calls OnWaitConditionChanged for every pending wait, including this one. Pre-fix,
        // MarkWarned() was evaluated eagerly as an IsSatisfied argument on each pass and permanently
        // burned the one-shot flag even though the branch was never actually scope-empty at the time.
        var elsewhere = new TestStateSource(new Person());
        monitor.Register(elsewhere);
        monitor.Unregister(elsewhere);
        monitor.Register(new TestStateSource(new Person()));
        Assert.Empty(recordingLogger.Warnings);

        // Now make the wait's own branch genuinely scope-empty: precisely the reparent scenario this
        // warning exists to diagnose.
        monitor.Unregister(source);

        // Assert
        Assert.Contains(recordingLogger.Warnings, message => message.Contains("has no in-scope source"));
    }

    [Fact]
    public async Task WhenASourceIsRegisteredMidWait_ThenItIsIncludedAndReBlocksTheWait()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var first = new TestStateSource(root);
        monitor.Register(first);
        monitor.CompleteSourceRegistration();
        first.ReportSynchronized();
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        await wait;

        // Act
        var second = new TestStateSource(root);
        monitor.Register(second);
        var secondWait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.False(secondWait.IsCompleted);
        second.ReportSynchronized();
        await secondWait;
    }

    [Fact]
    public async Task WhenEveryInScopeSourceIsStopped_ThenTheWaitCompletesVacuously()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();

        // Act
        source.ReportStopped();

        // Assert
        await root.WaitForSynchronizationAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenCancelled_ThenTheWaitPropagatesCancellation()
    {
        // Arrange
        var context = CreateContext();
        context.GetSourceMonitor().CompleteSourceRegistration();
        var root = new Person(context);
        using var cancellation = new CancellationTokenSource();

        // Act
        var wait = root.WaitForSynchronizationAsync(cancellation.Token);
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task WhenTheAnchorIsReparentedWithinTheTree_ThenAPendingWaitIsReEvaluated()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var left = new Person(context);
        var right = new Person(context);
        var moving = new Person();
        left.Mother = moving;
        var source = new TestStateSource(right);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();
        source.ReportSynchronized();
        var wait = moving.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        // Act
        left.Mother = null;
        right.Mother = moving;

        // Assert
        // This is the test that catches handler-ordering defects. A reparent within one tree fires
        // neither IsContextAttach nor IsContextDetach, and if the monitor runs before
        // ParentTrackingHandler it re-evaluates against stale parents, decides nothing changed, and
        // never looks again. The wait would then hang forever.
        await wait;
    }

    [Fact]
    public async Task WhenASourceIsDisposedWithoutBeingStopped_ThenStoppedPrecedesUnregistered()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));
        await source.StartAsync(CancellationToken.None);
        var received = new System.Collections.Concurrent.ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        source.Dispose();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceUnregistered));
        var kinds = received.Select(e => e.Kind).ToList();
        var stoppedIndex = kinds.FindIndex(k => k == SourceEventKind.StateChanged);
        var unregisteredIndex = kinds.FindIndex(k => k == SourceEventKind.SourceUnregistered);
        Assert.True(stoppedIndex >= 0 && stoppedIndex < unregisteredIndex);
    }

    [Fact]
    public async Task WhenASourceIsConstructedButNeverStarted_ThenItDoesNotAffectAWait()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var started = new TestStateSource(root);
        var neverStarted = new TestStateSource(root);
        monitor.Register(started);
        monitor.CompleteSourceRegistration();

        // Act
        started.ReportSynchronized();

        // Assert
        await root.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.DoesNotContain(neverStarted, monitor.Sources);
    }

    [Fact]
    public async Task WhenNoMonitorIsReachable_ThenTheWaitThrowsWithGuidance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Person(context);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => root.WaitForSynchronizationAsync(CancellationToken.None));
        Assert.Contains("WithSourceMonitoring", exception.Message);
    }

    [Fact]
    public void WhenOneMonitorsReleaseThrows_ThenTheOtherMonitorIsStillReleased()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithLifecycle()
            .WithSourceMonitoring();
        var child = InterceptorSubjectContext.Create().WithSourceMonitoring();
        child.AddFallbackContext(parent);

        var parentMonitor = parent.GetSourceMonitor();
        var childMonitor = child.GetServices<SourceMonitor>()[0];

        parentMonitor.CompleteSourceRegistration();
        childMonitor.CompleteSourceRegistration();

        var root = new Person(child);
        var throwing = new ThrowingScopeSource(root);
        childMonitor.Register(throwing);

        // Re-arms both monitors and gives each a pending wait, so the release below has something
        // to re-evaluate: it is that re-evaluation, not the release itself, that throws.
        var hold = child.DeferWaitCompletion();
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => hold.Dispose());

        // Assert - the throwing monitor's own count still dropped, and the other monitor's hold was
        // not stranded by the first one's exception.
        Assert.Equal("scope check failed", exception.Message);
        Assert.True(parentMonitor.IsRegistrationComplete);
        Assert.True(childMonitor.IsRegistrationComplete);
    }
}

/// <summary>A source whose RootSubject getter throws, to exercise exception-safety in wait re-evaluation.</summary>
internal sealed class ThrowingScopeSource : TestStateSource
{
    public ThrowingScopeSource(IInterceptorSubject rootSubject) : base(rootSubject)
    {
    }

    public override IInterceptorSubject RootSubject => throw new InvalidOperationException("scope check failed");
}

/// <summary>Captures every warning message logged through it, to assert on the monitor's diagnostics.</summary>
internal sealed class RecordingLogger : ILogger
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
        {
            Warnings.Add(formatter(state, exception));
        }
    }
}

/// <summary>Always resolves to the same <see cref="RecordingLogger"/>, regardless of category.</summary>
internal sealed class RecordingLoggerFactory(RecordingLogger logger) : ILoggerFactory
{
    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => logger;

    public void Dispose()
    {
    }
}
