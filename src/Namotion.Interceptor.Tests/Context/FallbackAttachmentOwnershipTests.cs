using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests.Context;

public class FallbackAttachmentOwnershipTests
{
    [Fact]
    public async Task WhenRemoveRunsDuringAttachCallbacks_ThenTheEdgeIsStillRemoved()
    {
        // Arrange: the attach callback parks, which is what a real attach does when the remover
        // already holds the lifecycle lock it needs. The edge is published by then, so a remover
        // arriving here finds a record whose attach has not completed.
        var parentContext = InterceptorSubjectContext.Create();
        using var attachStarted = new ManualResetEventSlim(false);
        using var releaseAttach = new ManualResetEventSlim(false);
        var interceptor = new ParkingLifecycleInterceptor(attachStarted, releaseAttach);
        parentContext.AddService<ILifecycleInterceptor>(interceptor);

        var childContext = ((IInterceptorSubject)new ContextProbeSubject()).Context;

        // Act
        var add = Task.Factory.StartNew(
            () => childContext.AddFallbackContext(parentContext), TaskCreationOptions.LongRunning);

        Assert.True(attachStarted.Wait(TimeSpan.FromSeconds(30)),
            "The attach callback never started, so the removal below would not race it.");

        var removed = childContext.RemoveFallbackContext(parentContext);
        releaseAttach.Set();
        var added = await add;

        // Assert: both calls report success, and the edge really is gone once everything settles.
        // The child executor owns no services, so resolving nothing means the fallback is gone.
        Assert.True(added);
        Assert.True(removed);
        await AsyncTestHelpers.WaitUntilAsync(
            () => childContext.GetServices<ILifecycleInterceptor>().IsEmpty,
            message: "The fallback edge survived a removal that raced the attach callbacks.");

        Assert.Equal(1, interceptor.AttachCount);
        Assert.Equal(1, interceptor.DetachCount);
    }

    [Fact]
    public void WhenDetachInterceptorThrows_ThenLaterInterceptorsStillReceiveDetach()
    {
        // Arrange
        var parentContext = InterceptorSubjectContext.Create();
        var throwing = new ThrowingDetachInterceptor();
        var following = new CountingLifecycleInterceptor();
        parentContext.AddService<ILifecycleInterceptor>(throwing);
        parentContext.AddService<ILifecycleInterceptor>(following);

        var childContext = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        Assert.True(childContext.AddFallbackContext(parentContext));

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => childContext.RemoveFallbackContext(parentContext));

        // Assert: the failure surfaces, the interceptor behind it still heard about the detach,
        // and the edge came out regardless.
        Assert.Equal("Detach failed.", exception.Message);
        Assert.Equal(1, following.DetachCount);
        Assert.True(childContext.GetServices<ILifecycleInterceptor>().IsEmpty);
    }

    private sealed class ParkingLifecycleInterceptor(
        ManualResetEventSlim attachStarted,
        ManualResetEventSlim releaseAttach) : ILifecycleInterceptor
    {
        private int _attachCount;
        private int _detachCount;

        public int AttachCount => Volatile.Read(ref _attachCount);

        public int DetachCount => Volatile.Read(ref _detachCount);

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
            Interlocked.Increment(ref _attachCount);
            attachStarted.Set();
            releaseAttach.Wait();
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            Interlocked.Increment(ref _detachCount);
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

    private sealed class ThrowingDetachInterceptor : ILifecycleInterceptor
    {
        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            throw new InvalidOperationException("Detach failed.");
        }
    }
}
