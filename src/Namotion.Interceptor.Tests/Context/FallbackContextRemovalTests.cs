using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests.Context;

/// <summary>
/// The executor runs the detach callbacks while the fallback edge is still registered, because they
/// resolve their handlers through the subject's own context and would find nothing once the edge is
/// gone. That leaves a window in which the edge is present but its removal is already under way, and
/// a removal of the same pair arriving inside that window has to be a no-op rather than a second run
/// of the callbacks: from a callback it would otherwise recurse until the stack is exhausted, and
/// from another thread it would fire the callbacks twice.
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

    private sealed class BlockingLifecycleInterceptor(ManualResetEventSlim insideCallback, ManualResetEventSlim release)
        : ILifecycleInterceptor
    {
        private int _detachCount;

        public int DetachCount => Volatile.Read(ref _detachCount);

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            Interlocked.Increment(ref _detachCount);
            insideCallback.Set();
            release.Wait();
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
