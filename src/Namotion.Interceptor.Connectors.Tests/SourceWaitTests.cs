using System.Collections.Concurrent;
using System.Reflection;
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
    public void WhenCompleteSourceRegistrationIsCalledConcurrentlyFromManyThreads_ThenTheInitialHoldIsReleasedExactlyOnce()
    {
        // Arrange
        // The idempotency guard (RegistrationHold.Dispose's own Interlocked.Exchange on _disposed,
        // via the initial hold that CompleteSourceRegistration disposes) is what must survive many
        // threads racing into CompleteSourceRegistration at once: if it let more than one thread
        // through, _registrationHolds would be decremented past zero and IsRegistrationComplete
        // would get stuck false, since nothing else would ever bring the count back up.
        var monitor = CreateContext().GetSourceMonitor();
        const int threadCount = 64;
        using var barrier = new Barrier(threadCount);

        // Raw threads, not the thread pool: Parallel.For/Task.Run schedule onto the pool, whose
        // throttled thread-injection heuristic can take many seconds to grow to threadCount
        // concurrent workers when they all immediately block on the barrier, making the test slow
        // without exercising any more concurrency than a handful of real threads would.
        var threads = new Thread[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                monitor.CompleteSourceRegistration();
            });
        }

        // Act
        foreach (var thread in threads)
        {
            thread.Start();
        }
        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Assert
        Assert.True(monitor.IsRegistrationComplete);
        Assert.Equal(0, GetRegistrationHolds(monitor));
    }

    [Fact]
    public void WhenManyDeferWaitCompletionHoldsAreTakenAndDisposedConcurrently_ThenTheCountReturnsToZero()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();
        monitor.CompleteSourceRegistration(); // release the initial hold so the baseline is zero
        const int threadCount = 32;
        const int perThreadIterations = 500;
        using var barrier = new Barrier(threadCount);

        // Raw threads: see the comment in the test above for why the thread pool is avoided here.
        var threads = new Thread[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < perThreadIterations; iteration++)
                {
                    using var hold = monitor.DeferWaitCompletion();
                }
            });
        }

        // Act
        foreach (var thread in threads)
        {
            thread.Start();
        }
        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Assert
        // The count must land back at exactly zero: never negative (a lost double-release) and
        // never stuck positive (a leaked hold that was never disposed).
        Assert.True(monitor.IsRegistrationComplete);
        Assert.Equal(0, GetRegistrationHolds(monitor));
    }

    private static int GetRegistrationHolds(SourceMonitor monitor)
    {
        var field = typeof(SourceMonitor).GetField("_registrationHolds", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (int)field.GetValue(monitor)!;
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
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
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
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
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
        await left.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(SourceState.Connecting, broken.State);
    }

    [Fact]
    public async Task WhenNoInScopeSourceIsRegistered_ThenTheWaitCompletesVacuously()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        monitor.CompleteSourceRegistration();

        // Act - once registration is complete, an empty scope is no longer ambiguous between "no
        // source yet" and "no source ever": it definitively means this branch is local-only, so the
        // wait completes immediately instead of blocking forever (consistent with the all-Stopped
        // rule, which already completes vacuously rather than hanging).
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenNoInScopeSourceIsRegisteredAndRegistrationIsIncomplete_ThenTheWaitStillBlocks()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        // Registration is deliberately left incomplete here: only the post-signal case changes to
        // vacuous completion. Before the signal, an empty scope is still ambiguous between "no
        // source yet" and "no source ever", so it must keep blocking - this is the startup
        // protection the empty-scope rule cannot give up.

        // Act
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        await Task.Yield();
        Assert.False(wait.IsCompleted);
        monitor.CompleteSourceRegistration();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenScopeBecomesEmptyAfterUnrelatedReEvaluations_ThenTheEmptyScopeWarningStillFires()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var recordingLogger = new RecordingLogger();
        context.AddService<ILoggerFactory>(new RecordingLoggerFactory(recordingLogger));

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
        // warning exists to diagnose. Registration is already complete, so this also makes the wait
        // vacuously satisfied (see IsSatisfied), not just warned.
        monitor.Unregister(source);

        // Immediately, on the same thread and before awaiting anything, trigger another
        // re-evaluation pass. wait.Complete() above scheduled its continuation asynchronously
        // (RunContinuationsAsynchronously), so the wait can still be sitting in _waits, not yet
        // removed, when this second pass runs - exactly the window in which a regression that
        // dropped the MarkWarned guard would log the same empty-scope warning a second time.
        monitor.Register(new TestStateSource(new Person()));

        // Assert
        Assert.Single(recordingLogger.Warnings, message => message.Contains("has no in-scope source"));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenTheSameStoppedWaitIsReEvaluatedBeforeRemoval_ThenTheAllStoppedWarningLogsOnlyOnce()
    {
        // Arrange
        // Mirrors WhenScopeBecomesEmptyAfterUnrelatedReEvaluations_ThenTheEmptyScopeWarningStillFires
        // above, but for the all-Stopped warning instead of the empty-scope one: unlike the
        // empty-scope warning, this one had no per-wait one-shot guard, so it could re-log in the
        // window between a wait completing (Stopped makes the branch vacuously satisfied) and its
        // continuation actually removing it from _waits.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var recordingLogger = new RecordingLogger();
        context.AddService<ILoggerFactory>(new RecordingLoggerFactory(recordingLogger));

        var anchor = new Person(context);
        var source = new TestStateSource(anchor);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();

        // Matched but not yet satisfied (Connecting), so this becomes a real, tracked PendingWait.
        var wait = anchor.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        // Act - Stopped makes every in-scope source terminal, so the branch is vacuously satisfied
        // and OnWaitConditionChanged's re-evaluation logs the all-Stopped warning once.
        source.ReportStopped();

        // Immediately trigger a second re-evaluation pass, on the same thread, before wait.Complete()
        // above's asynchronously scheduled continuation (RunContinuationsAsynchronously) has had a
        // chance to remove this wait from _waits - exactly the window in which a regression that
        // dropped a one-shot guard on this warning would log it a second time.
        monitor.Register(new TestStateSource(new Person()));

        // Assert
        Assert.Single(recordingLogger.Warnings, message => message.Contains("every in-scope source stopped"));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WhenNoSubscriptionWasEverMade_ThenTheEmptyScopeWarningStillFires()
    {
        // Arrange
        // The Getting Started sample never calls Subscribe: it only registers a source and awaits
        // WaitForSynchronizationAsync. The logger must resolve on demand, from whichever call site
        // needs it first, not only from Subscribe - otherwise this exact sample never sees its one
        // diagnostic for a misconfigured wait.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var recordingLogger = new RecordingLogger();
        context.AddService<ILoggerFactory>(new RecordingLoggerFactory(recordingLogger));
        monitor.CompleteSourceRegistration();

        var anchor = new Person(context);

        // Act - WaitForSynchronizationAsync's fast-path check passes a null PendingWait to
        // IsSatisfied, and a null wait always warns (no dedup needed for an evaluation that is
        // never re-run), so the empty-scope warning fires right here, on the very first evaluation,
        // without ever calling Subscribe, without allocating a PendingWait, and without needing a
        // later re-evaluation.
        var wait = anchor.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.True(wait.IsCompletedSuccessfully);
        Assert.Contains(recordingLogger.Warnings, message => message.Contains("has no in-scope source"));
    }

    [Fact]
    public async Task WhenOneWaitsReEvaluationThrows_ThenOtherPendingWaitsAreStillReEvaluated()
    {
        // Arrange
        var context = CreateContext();
        context.AddService<ILoggerFactory>(new ThrowingLoggerFactory());
        var monitor = context.GetSourceMonitor();

        var healthyRoot = new Person(context);
        var healthySource = new TestStateSource(healthyRoot);
        monitor.Register(healthySource);

        var stoppedRoot = new Person(context);
        var stoppedSource = new TestStateSource(stoppedRoot);
        monitor.Register(stoppedSource);
        stoppedSource.ReportStopped();

        // A hold keeps registration incomplete while both waits below are created, so their
        // fast-path IsSatisfied check short-circuits on IsRegistrationComplete without ever
        // reaching the all-Stopped warning - that only happens on the re-evaluation pass triggered
        // by disposing the hold, further down. (An empty-scope wait cannot be used for this instead:
        // since registration-complete now makes an empty scope vacuously satisfied, such a wait
        // could never stay pending long enough to be re-evaluated a second time.)
        var hold = monitor.DeferWaitCompletion();
        monitor.CompleteSourceRegistration();

        // Added to _waits before the healthy wait below, so a loop with no per-wait isolation
        // reaches this one first. Its only in-scope source is Stopped, which makes its
        // re-evaluation log the all-Stopped warning (unconditional, unlike the once-per-wait
        // empty-scope one) - and the logger this test installs throws from LogWarning, so
        // re-evaluating this wait is what throws, every time it is reached.
        var stoppedWait = stoppedRoot.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(stoppedWait.IsCompleted);

        // A second, later wait whose own re-evaluation never touches the logger: it has an
        // in-scope source that is not yet synchronized, so IsSatisfied returns false without ever
        // reaching a warning branch - stoppedSource is not in healthyRoot's scope at all.
        var healthyWait = healthyRoot.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(healthyWait.IsCompleted);

        // Releasing the hold makes registration complete and triggers a single re-evaluation pass
        // over every pending wait, including stoppedWait, whose all-Stopped warning throws.
        var releaseException = Assert.Throws<InvalidOperationException>(() => hold.Dispose());
        Assert.Equal("logging is broken", releaseException.Message);

        // Act - transitioning healthySource triggers a further re-evaluation pass over every
        // pending wait. Without per-wait isolation, the throw while re-evaluating stoppedWait
        // (first in the list) would abort the pass before healthyWait (second) is ever looked at
        // again, which would leave it pending forever - a lost wakeup.
        healthySource.ReportSynchronized();

        // Assert
        await healthyWait.WaitAsync(TimeSpan.FromSeconds(5));
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
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        var second = new TestStateSource(root);
        monitor.Register(second);
        var secondWait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.False(secondWait.IsCompleted);
        second.ReportSynchronized();
        await secondWait.WaitAsync(TimeSpan.FromSeconds(5));
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
        await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenCancelled_ThenTheWaitPropagatesCancellation()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        // An in-scope, unsynchronized source keeps the wait genuinely pending: an empty scope would
        // instead complete vacuously once registration is complete (see IsSatisfied), leaving
        // nothing for cancellation to interrupt.
        monitor.Register(new TestStateSource(root));
        monitor.CompleteSourceRegistration();
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

        // Keeps moving's scope non-empty (so the wait below genuinely blocks instead of completing
        // vacuously - see IsSatisfied's empty-scope rule) while moving sits under left: rooted at
        // left, this source drops out of moving's scope the instant the reparent below moves it
        // away, the same reparent that brings the real source into scope.
        var keepAlive = new TestStateSource(left);
        monitor.Register(keepAlive);

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
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
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
    public async Task WhenASourceRegistersAndImmediatelyTransitions_ThenSourceRegisteredPrecedesStateChanged()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));
        var source = new TestStateSource(new Person(context));

        // Act
        monitor.Register(source);
        source.ReportSynchronized();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.StateChanged));
        var kinds = received.Select(e => e.Kind).ToList();
        var registeredIndex = kinds.FindIndex(k => k == SourceEventKind.SourceRegistered);
        var stateChangedIndex = kinds.FindIndex(k => k == SourceEventKind.StateChanged);
        Assert.True(registeredIndex >= 0 && registeredIndex < stateChangedIndex);
    }

    [Fact]
    public async Task WhenAHoldIsTakenWhileABranchIsAlreadySynchronized_ThenANewWaitBlocksUntilTheHoldIsDisposed()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();
        source.ReportSynchronized();

        // Act - DeferWaitCompletion re-arms IsRegistrationComplete even though the branch itself has
        // nothing left to synchronize: a wait created while the hold is outstanding must still block
        // purely on the hold, and only unblock once it is disposed.
        var hold = monitor.DeferWaitCompletion();
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.False(wait.IsCompleted);
        hold.Dispose();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
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
        await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
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
    public async Task WhenAWaitRacesASourceTransitionToSynchronized_ThenTheWaitAlwaysCompletes()
    {
        // Arrange
        // Reproduces a lost wakeup: OnWaitConditionChanged used to gate itself on a lock-free
        // "_waits is empty" read. That read could observe the not-yet-published wait list while
        // WaitForSynchronizationAsync's own check-and-add was still in flight under _lock, so the
        // signal would return without ever completing the wait it just missed - and nothing later
        // would re-evaluate it. A barrier lines up the two racing operations on every iteration so
        // the narrow window gets exercised repeatedly instead of relying on incidental timing.
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();

        const int iterations = 20_000;
        using var barrier = new Barrier(2);

        var transitionThread = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                {
                    return;
                }

                source.ReportSynchronized();
            }
        })
        {
            IsBackground = true
        };
        transitionThread.Start();

        // Act & Assert
        for (var i = 0; i < iterations; i++)
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30)));

            var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

            // A bounded timeout so a regression fails this test instead of wedging the run.
            await wait.WaitAsync(TimeSpan.FromSeconds(5));

            // Reset for the next iteration. TransitionTo allows Synchronized -> Connecting, and
            // wait's removal from the monitor's pending-wait list happens before the awaited task
            // above completes (see PendingWait.AwaitAsync), so the monitor has no leftover wait
            // state carried into the next iteration.
            source.ReportConnecting();
        }

        transitionThread.Join(TimeSpan.FromSeconds(30));
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

/// <summary>A logger whose LogWarning call throws, to exercise exception-safety in wait re-evaluation.</summary>
internal sealed class ThrowingLogger : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
        {
            throw new InvalidOperationException("logging is broken");
        }
    }
}

/// <summary>Always resolves to a fresh <see cref="ThrowingLogger"/>, regardless of category.</summary>
internal sealed class ThrowingLoggerFactory : ILoggerFactory
{
    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new ThrowingLogger();

    public void Dispose()
    {
    }
}

