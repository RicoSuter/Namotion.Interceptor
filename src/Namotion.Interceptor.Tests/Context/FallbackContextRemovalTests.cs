using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests.Context;

/// <summary>
/// Removals of a fallback context pair that arrive while the same pair's removal is already under
/// way. The rationale is documented on <see cref="InterceptorExecutor"/>.
/// </summary>
public class FallbackContextRemovalTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);

    [Fact]
    public void WhenDetachCallbackRemovesTheSameFallbackContextAgain_ThenDetachCallbacksRunOnce()
    {
        // Arrange: the re-entry is bounded because an unbounded one exhausts the stack, which
        // cannot be caught and would kill the test host instead of failing this test.
        var fallbackContext = InterceptorSubjectContext.Create();
        var interceptor = new ReenteringLifecycleInterceptor(fallbackContext, maximumReentries: 5);
        fallbackContext.AddService<ILifecycleInterceptor>(interceptor);

        var car = new Car(fallbackContext);
        var subjectContext = ((IInterceptorSubject)car).Context;

        // Act
        var removed = subjectContext.RemoveFallbackContext(fallbackContext);

        // Assert
        Assert.Equal(1, interceptor.DetachCount);
        Assert.Equal(new[] { false }, interceptor.NestedRemovalResults);
        Assert.True(removed);
        Assert.Empty(subjectContext.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public async Task WhenTwoThreadsRemoveTheSameFallbackContextConcurrently_ThenDetachCallbacksRunOnce()
    {
        // Arrange: the first remover is parked inside the detach callback, so the second one
        // arrives while the edge is still registered but its removal is already under way.
        var fallbackContext = InterceptorSubjectContext.Create();
        using var insideCallback = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var interceptor = new BlockingLifecycleInterceptor(insideCallback, release);
        fallbackContext.AddService<ILifecycleInterceptor>(interceptor);

        var car = new Car(fallbackContext);
        var subjectContext = ((IInterceptorSubject)car).Context;

        var firstRemoval = Task.Factory.StartNew(
            () => subjectContext.RemoveFallbackContext(fallbackContext),
            TaskCreationOptions.LongRunning);

        Assert.True(insideCallback.Wait(WaitBudget), "The first remover never reached the detach callback.");

        // Act
        var secondRemoval = Task.Factory.StartNew(
            () => subjectContext.RemoveFallbackContext(fallbackContext),
            TaskCreationOptions.LongRunning);

        // The second remover either returns, or it is parked in the callback as well, which is the
        // double fire this test exists to catch. Both outcomes are observable, so no delay is needed.
        await AsyncTestHelpers.WaitUntilAsync(
            () => secondRemoval.IsCompleted || interceptor.DetachCount >= 2,
            WaitBudget);

        release.Set();
        var results = await Task.WhenAll(firstRemoval, secondRemoval).WaitAsync(WaitBudget);

        // Assert
        Assert.Equal(1, interceptor.DetachCount);
        Assert.Single(results, result => result);
        Assert.Empty(subjectContext.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenDetachCallbackThrows_ThenTheSamePairCanBeRemovedAgain()
    {
        // Arrange
        var fallbackContext = InterceptorSubjectContext.Create();
        var interceptor = new ThrowingLifecycleInterceptor { ThrowOnDetach = true };
        fallbackContext.AddService<ILifecycleInterceptor>(interceptor);

        var car = new Car(fallbackContext);
        var subjectContext = ((IInterceptorSubject)car).Context;

        Assert.Throws<InvalidOperationException>(() => subjectContext.RemoveFallbackContext(fallbackContext));
        Assert.Single(subjectContext.GetServices<ILifecycleInterceptor>());
        interceptor.ThrowOnDetach = false;

        // Act
        var removed = subjectContext.RemoveFallbackContext(fallbackContext);

        // Assert
        Assert.True(removed);
        Assert.Equal(2, interceptor.DetachCount);
        Assert.Empty(subjectContext.GetServices<ILifecycleInterceptor>());
    }

    /// <summary>
    /// The window between a removal being published and its invalidation walk finishing. The walk
    /// is parked on a using set the test holds while the pair is added back, so a removal issued
    /// after that add has to own the new edge, and the parked removal finishing late must not drop
    /// that ownership.
    /// </summary>
    [Fact]
    public async Task WhenPairIsAddedBackWhileItsRemovalIsStillInvalidating_ThenTheNextRemovalOwnsTheNewEdge()
    {
        // Arrange: the using chain subject <- first <- second <- third makes the removal's walk lock
        // second's using set, and the set exists only because third resolves through second.
        var fallbackContext = InterceptorSubjectContext.Create();
        using var insideCallback = new ManualResetEventSlim(false);
        using var releaseCallback = new ManualResetEventSlim(false);
        var interceptor = new BlockingLifecycleInterceptor(insideCallback, releaseCallback, blockOnDetach: 2);
        fallbackContext.AddService<ILifecycleInterceptor>(interceptor);

        var car = new Car(fallbackContext);
        var subjectContext = (InterceptorSubjectContext)((IInterceptorSubject)car).Context;

        var firstUser = InterceptorSubjectContext.Create();
        firstUser.AddFallbackContext(subjectContext);
        var secondUser = InterceptorSubjectContext.Create();
        secondUser.AddFallbackContext(firstUser);
        var thirdUser = InterceptorSubjectContext.Create();
        thirdUser.AddFallbackContext(secondUser);

        var firstUsersSet = ContextStateReflection.GetUsedByContexts(firstUser);
        var secondUsersSet = ContextStateReflection.GetUsedByContexts(secondUser);
        var secondUsersState = ContextStateReflection.GetState(secondUser);

        using var lockHeld = new ManualResetEventSlim(false);
        using var releaseLock = new ManualResetEventSlim(false);
        var lockHolder = new Thread(() =>
        {
            lock (secondUsersSet)
            {
                lockHeld.Set();
                releaseLock.Wait();
            }
        }) { IsBackground = true };
        lockHolder.Start();
        Assert.True(lockHeld.Wait(WaitBudget), "The lock holder never took the using set.");

        var parkedRemoval = Task.FromResult(false);
        var detachSecondUser = Task.FromResult(false);
        var nextRemoval = Task.FromResult(false);
        bool laterRemoved;
        try
        {
            // The walk invalidates second right before it locks second's using set.
            parkedRemoval = Task.Factory.StartNew(
                () => subjectContext.RemoveFallbackContext(fallbackContext),
                TaskCreationOptions.LongRunning);
            await AsyncTestHelpers.WaitUntilAsync(
                () => !ReferenceEquals(ContextStateReflection.GetState(secondUser), secondUsersState),
                WaitBudget);

            // Dropping second from first's using set keeps the add's own walk off the held lock.
            // This removal's walk parks on the same lock, after it has unregistered.
            detachSecondUser = Task.Factory.StartNew(
                () => secondUser.RemoveFallbackContext(firstUser),
                TaskCreationOptions.LongRunning);
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    lock (firstUsersSet)
                    {
                        return !firstUsersSet.Contains(secondUser);
                    }
                },
                WaitBudget);

            Assert.True(subjectContext.AddFallbackContext(fallbackContext));

            // Act: the next removal parks in its detach callback, then the first one finishes.
            nextRemoval = Task.Factory.StartNew(
                () => subjectContext.RemoveFallbackContext(fallbackContext),
                TaskCreationOptions.LongRunning);
            await AsyncTestHelpers.WaitUntilAsync(() => nextRemoval.IsCompleted || insideCallback.IsSet, WaitBudget);
            Assert.False(nextRemoval.IsCompleted, "The removal issued after the add returned was refused.");

            releaseLock.Set();
            await Task.WhenAll(parkedRemoval, detachSecondUser).WaitAsync(WaitBudget);

            laterRemoved = subjectContext.RemoveFallbackContext(fallbackContext);
        }
        finally
        {
            releaseLock.Set();
            releaseCallback.Set();
            await Task.WhenAll(parkedRemoval, detachSecondUser, nextRemoval).WaitAsync(WaitBudget);
        }

        // Assert
        Assert.True(await parkedRemoval);
        Assert.True(await nextRemoval);
        Assert.False(laterRemoved);
        Assert.Equal(2, interceptor.DetachCount);
        Assert.Empty(subjectContext.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public async Task WhenRemovalIsInterruptedBeforeItCommits_ThenTheSamePairCanBeRemovedAgain()
    {
        // Arrange: a blocking service factory holds the subject context's mutation lock, so the
        // removal runs its callbacks and then waits for that lock, where it is interrupted.
        var fallbackContext = InterceptorSubjectContext.Create();
        var interceptor = new CountingLifecycleInterceptor();
        fallbackContext.AddService<ILifecycleInterceptor>(interceptor);

        var car = new Car(fallbackContext);
        var subjectContext = ((IInterceptorSubject)car).Context;

        using var lockHeld = new ManualResetEventSlim(false);
        using var releaseLock = new ManualResetEventSlim(false);
        var lockHolder = Task.Factory.StartNew(
            () => subjectContext.TryAddService(
                () =>
                {
                    lockHeld.Set();
                    releaseLock.Wait();
                    return new MarkerService();
                },
                _ => false),
            TaskCreationOptions.LongRunning);
        Assert.True(lockHeld.Wait(WaitBudget), "The service factory never took the mutation lock.");

        Exception? removalException = null;
        var removal = new Thread(() =>
        {
            try
            {
                subjectContext.RemoveFallbackContext(fallbackContext);
            }
            catch (ThreadInterruptedException exception)
            {
                removalException = exception;
            }
        }) { IsBackground = true };

        try
        {
            removal.Start();
            await AsyncTestHelpers.WaitUntilAsync(() => interceptor.DetachCount == 1, WaitBudget);
            removal.Interrupt();
            Assert.True(removal.Join(WaitBudget), "The interrupted removal never returned.");
        }
        finally
        {
            releaseLock.Set();
            await lockHolder.WaitAsync(WaitBudget);
        }

        Assert.IsType<ThreadInterruptedException>(removalException);
        Assert.Single(subjectContext.GetServices<ILifecycleInterceptor>());

        // Act
        var removed = subjectContext.RemoveFallbackContext(fallbackContext);

        // Assert
        Assert.True(removed);
        Assert.Equal(2, interceptor.DetachCount);
        Assert.Empty(subjectContext.GetServices<ILifecycleInterceptor>());
    }

    /// <summary>
    /// The guard is per pair, not per executor: a detach callback that removes a different fallback
    /// context of the same subject is a legitimate nested removal and has to go through.
    /// </summary>
    [Fact]
    public void WhenDetachCallbackRemovesAnotherFallbackContext_ThenThatRemovalProceeds()
    {
        // Arrange
        var firstFallbackContext = InterceptorSubjectContext.Create();
        var secondFallbackContext = InterceptorSubjectContext.Create();
        var interceptor = new ReenteringLifecycleInterceptor(firstFallbackContext, maximumReentries: 1);
        secondFallbackContext.AddService<ILifecycleInterceptor>(interceptor);

        var car = new Car(firstFallbackContext);
        var subjectContext = ((IInterceptorSubject)car).Context;
        subjectContext.AddFallbackContext(secondFallbackContext);

        // Act
        var removed = subjectContext.RemoveFallbackContext(secondFallbackContext);

        // Assert
        Assert.True(removed);
        Assert.Equal(new[] { true }, interceptor.NestedRemovalResults);
        Assert.False(subjectContext.RemoveFallbackContext(firstFallbackContext));
        Assert.False(subjectContext.RemoveFallbackContext(secondFallbackContext));
    }

    /// <summary>
    /// Pins the ordering the guard exists to preserve: attach callbacks run once the edge is
    /// registered and detach callbacks run while it still is, so both resolve through it.
    /// </summary>
    [Fact]
    public void WhenFallbackContextIsAddedAndRemoved_ThenBothCallbacksResolveThroughTheEdge()
    {
        // Arrange: the marker lives on the fallback context only, so the subject's context sees it
        // exactly while the edge is registered.
        var fallbackContext = InterceptorSubjectContext.Create();
        fallbackContext.AddService(new MarkerService());
        var interceptor = new ProbingLifecycleInterceptor();
        fallbackContext.AddService<ILifecycleInterceptor>(interceptor);

        // Act
        var car = new Car(fallbackContext);
        var subjectContext = ((IInterceptorSubject)car).Context;
        subjectContext.RemoveFallbackContext(fallbackContext);

        // Assert
        Assert.Equal(1, interceptor.MarkersVisibleDuringAttach);
        Assert.Equal(1, interceptor.MarkersVisibleDuringDetach);
        Assert.Empty(subjectContext.GetServices<MarkerService>());
    }

    private sealed class ReenteringLifecycleInterceptor(IInterceptorSubjectContext contextToRemove, int maximumReentries)
        : ILifecycleInterceptor
    {
        private int _reentries;

        public int DetachCount { get; private set; }

        public List<bool> NestedRemovalResults { get; } = [];

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            DetachCount++;
            if (_reentries < maximumReentries)
            {
                _reentries++;
                NestedRemovalResults.Add(subject.Context.RemoveFallbackContext(contextToRemove));
            }
        }
    }

    private sealed class ThrowingLifecycleInterceptor : ILifecycleInterceptor
    {
        public bool ThrowOnDetach { get; set; }

        public int DetachCount { get; private set; }

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            DetachCount++;
            if (ThrowOnDetach)
            {
                throw new InvalidOperationException("Detach failed.");
            }
        }
    }

    private sealed class CountingLifecycleInterceptor : ILifecycleInterceptor
    {
        private int _detachCount;

        public int DetachCount => Volatile.Read(ref _detachCount);

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            Interlocked.Increment(ref _detachCount);
        }
    }

    /// <summary>Parks the detach callback with the given number, or every one when it is zero.</summary>
    private sealed class BlockingLifecycleInterceptor(
        ManualResetEventSlim insideCallback, ManualResetEventSlim release, int blockOnDetach = 0)
        : ILifecycleInterceptor
    {
        private int _detachCount;

        public int DetachCount => Volatile.Read(ref _detachCount);

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            var detachNumber = Interlocked.Increment(ref _detachCount);
            if (blockOnDetach == 0 || detachNumber == blockOnDetach)
            {
                insideCallback.Set();
                release.Wait();
            }
        }
    }

    private sealed class ProbingLifecycleInterceptor : ILifecycleInterceptor
    {
        public int MarkersVisibleDuringAttach { get; private set; }

        public int MarkersVisibleDuringDetach { get; private set; }

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
            MarkersVisibleDuringAttach = subject.Context.GetServices<MarkerService>().Length;
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            MarkersVisibleDuringDetach = subject.Context.GetServices<MarkerService>().Length;
        }
    }

    private sealed class MarkerService;
}
