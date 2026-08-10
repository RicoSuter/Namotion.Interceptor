namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceTargetTests
{
    /// <summary>
    /// One round of the append race does not reliably detect an unsynchronised chain, so a single
    /// round per continuous integration run would let a regression through. Measured against a build
    /// with the append lock removed: one round failed 13 of 15 runs, twelve rounds failed 15 of 15.
    /// </summary>
    private const int AppendRaceRounds = 12;

    [Fact]
    public async Task WhenTransitionsAreAppendedConcurrently_ThenTheyNeverOverlap()
    {
        // Arrange - an unsynchronised "_tail = _tail.ContinueWith(...)" is a read-modify-write and
        // loses an assignment under contention, running several transitions on one target at once.
        var maximumConcurrent = 0;

        // Act
        for (var round = 0; round < AppendRaceRounds; round++)
        {
            maximumConcurrent = Math.Max(maximumConcurrent, await RunAppendRaceAsync());
        }

        // Assert
        Assert.Equal(1, maximumConcurrent);
    }

    /// <summary>
    /// Races eight appends against one target while its head transition is held, and returns the
    /// highest number of transition bodies that were ever running at once.
    /// </summary>
    private static async Task<int> RunAppendRaceAsync()
    {
        var target = new HostedServiceTarget(factory: null, subject: null);
        var head = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var maximumConcurrent = 0;
        var sync = new object();

        var stall = target.AppendAsync(async _ => await head.Task, CancellationToken.None);

        async Task BodyAsync(CancellationToken cancellationToken)
        {
            lock (sync)
            {
                concurrent++;
                maximumConcurrent = Math.Max(maximumConcurrent, concurrent);
            }

            await Task.Yield();

            lock (sync)
            {
                concurrent--;
            }
        }

        // Real threads and a barrier rather than Task.Run, which unwraps Func<Task> and would wait
        // for each transition instead of just for the append.
        const int appenderCount = 8;
        var transitions = new Task[appenderCount];
        var threads = new Thread[appenderCount];
        using var barrier = new Barrier(appenderCount);

        for (var index = 0; index < appenderCount; index++)
        {
            var slot = index;
            threads[slot] = new Thread(() =>
            {
                barrier.SignalAndWait();
                transitions[slot] = target.AppendAsync(BodyAsync, CancellationToken.None);
            });

            threads[slot].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        head.SetResult();
        await stall;
        await Task.WhenAll(transitions);

        lock (sync)
        {
            return maximumConcurrent;
        }
    }

    [Fact]
    public async Task WhenATransitionThrows_ThenTheChainContinues()
    {
        // Arrange - a faulted tail would raise UnobservedTaskException for every dropped fire and
        // forget transition, and the fault would surface nowhere at all.
        var target = new HostedServiceTarget(factory: null, subject: null);
        var secondRan = false;

        // Act
        await target.AppendAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None);
        await target.AppendAsync(_ =>
        {
            secondRan = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Assert
        Assert.True(secondRan);
    }

    [Fact]
    public async Task WhenATransitionIsAppended_ThenItDoesNotRunOnTheAppendingThread()
    {
        // Arrange - appends happen while LifecycleInterceptor holds its lock, so an inline
        // continuation would run a transition body under that lock. The append runs on a dedicated
        // thread rather than the test's own: xunit runs tests on pool threads, and a body queued to
        // the pool can be picked up by the very thread that appended once that thread parks on the
        // await below, which reads as inline execution without being it.
        var target = new HostedServiceTarget(factory: null, subject: null);
        var ranInline = false;
        Task? transition = null;

        var appendingThread = new Thread(() =>
        {
            var appendingThreadId = Environment.CurrentManagedThreadId;
            transition = target.AppendAsync(_ =>
            {
                ranInline = Environment.CurrentManagedThreadId == appendingThreadId;
                return Task.CompletedTask;
            }, CancellationToken.None);
        });

        // Act
        appendingThread.Start();
        appendingThread.Join();
        await transition!;

        // Assert
        Assert.False(ranInline);
    }

    [Fact]
    public async Task WhenTheTransitionGateIsSet_ThenTheTransitionIsHeld()
    {
        // Arrange - the seam the ordering and race tests need, so they do not depend on timing
        var target = new HostedServiceTarget(factory: null, subject: null);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ran = false;

        target.TransitionGate = () => release.Task;

        // Act
        var transition = target.AppendAsync(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Assert
        Assert.False(ran);
        release.SetResult();
        await transition;
        Assert.True(ran);
    }

    [Fact]
    public void WhenOwnershipIsTakenTwiceByTheSameHandler_ThenItSucceeds()
    {
        // Arrange - a re-attach arriving before the release must not read as "lost to another handler"
        var target = new HostedServiceTarget(factory: null, subject: null);
        var handler = new HostedServiceHandler(() => null);

        // Act
        var first = target.TryTakeOwnership(handler, out var firstTaken);
        var second = target.TryTakeOwnership(handler, out var secondTaken);

        // Assert - both succeed, but only the first installed the owner. A caller that has to undo
        // its own take must not undo the earlier one, whose instance may still be running.
        Assert.True(first);
        Assert.True(second);
        Assert.True(firstTaken);
        Assert.False(secondTaken);
    }

    [Fact]
    public void WhenASecondHandlerTakesOwnership_ThenItFails()
    {
        // Arrange
        var target = new HostedServiceTarget(factory: null, subject: null);
        var first = new HostedServiceHandler(() => null);
        var second = new HostedServiceHandler(() => null);
        target.TryTakeOwnership(first, out _);

        // Act
        var taken = target.TryTakeOwnership(second, out _);

        // Assert
        Assert.False(taken);
        Assert.Same(first, target.Owner);
    }

    [Fact]
    public void WhenOwnershipIsReleased_ThenAnotherHandlerCanTakeIt()
    {
        // Arrange - release on context detach is what lets a subject move between contexts
        var target = new HostedServiceTarget(factory: null, subject: null);
        var first = new HostedServiceHandler(() => null);
        var second = new HostedServiceHandler(() => null);
        target.TryTakeOwnership(first, out _);

        // Act
        target.ReleaseOwnership(first);
        var taken = target.TryTakeOwnership(second, out _);

        // Assert
        Assert.True(taken);
        Assert.Same(second, target.Owner);
    }
}
