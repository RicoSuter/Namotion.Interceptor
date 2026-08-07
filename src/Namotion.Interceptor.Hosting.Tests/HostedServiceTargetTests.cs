namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceTargetTests
{
    [Fact]
    public async Task WhenTransitionsAreAppendedConcurrently_ThenTheyNeverOverlap()
    {
        // Arrange - an unsynchronised "_tail = _tail.ContinueWith(...)" is a read-modify-write and
        // loses an assignment under contention, running several transitions on one target at once.
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

        // Act - appends race from several threads while the head transition is held.
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

        // Assert
        Assert.Equal(1, maximumConcurrent);
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
        // continuation would run user code under that lock.
        var target = new HostedServiceTarget(factory: null, subject: null);
        var appendingThread = Environment.CurrentManagedThreadId;
        var ranInline = false;

        // Act
        await target.AppendAsync(_ =>
        {
            ranInline = Environment.CurrentManagedThreadId == appendingThread;
            return Task.CompletedTask;
        }, CancellationToken.None);

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
        var first = target.TryTakeOwnership(handler);
        var second = target.TryTakeOwnership(handler);

        // Assert
        Assert.True(first);
        Assert.True(second);
    }

    [Fact]
    public void WhenASecondHandlerTakesOwnership_ThenItFails()
    {
        // Arrange
        var target = new HostedServiceTarget(factory: null, subject: null);
        var first = new HostedServiceHandler(() => null);
        var second = new HostedServiceHandler(() => null);
        target.TryTakeOwnership(first);

        // Act
        var taken = target.TryTakeOwnership(second);

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
        target.TryTakeOwnership(first);

        // Act
        target.ReleaseOwnership(first);
        var taken = target.TryTakeOwnership(second);

        // Assert
        Assert.True(taken);
        Assert.Same(second, target.Owner);
    }
}
