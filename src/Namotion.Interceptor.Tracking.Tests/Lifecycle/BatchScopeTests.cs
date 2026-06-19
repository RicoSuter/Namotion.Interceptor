using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class BatchScopeTests
{
    [Fact]
    public void BasicBatchScope_DetachDeferredThenProcessedOnDispose()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — detach within batch scope
        var scope = lifecycle.CreateBatchScope(context);
        parent.Mother = null;

        // Assert — still in registry during scope
        Assert.True(idRegistry.TryGetSubjectById("childId", out _));

        // Act — dispose scope
        scope.Dispose();

        // Assert — now removed (genuinely orphaned)
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void MoveBetweenProperties_SubjectStaysRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — move child from Mother to Father within batch scope
        using (lifecycle.CreateBatchScope(context))
        {
            parent.Mother = null;   // detach (deferred)
            parent.Father = child;  // reattach to different property
        }

        // Assert — child stays registered (was moved, not removed)
        Assert.True(idRegistry.TryGetSubjectById("childId", out var found));
        Assert.Same(child, found);
    }

    [Fact]
    public void GenuineRemoval_CleanedUpOnDispose()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — detach without reattach
        using (lifecycle.CreateBatchScope(context))
        {
            parent.Mother = null;
        }

        // Assert — genuinely orphaned, removed on dispose
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void NoScope_ExistingBehaviorUnchanged()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — detach without any scope
        parent.Mother = null;

        // Assert — immediate removal
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void NestedScopes_ProcessedOnOuterDispose()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — nested scopes
        var outer = lifecycle.CreateBatchScope(context);
        var inner = lifecycle.CreateBatchScope(context);

        parent.Mother = null;

        // Dispose inner — no processing yet
        inner.Dispose();
        Assert.True(idRegistry.TryGetSubjectById("childId", out _));

        // Dispose outer — processes deferred detaches
        outer.Dispose();
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void ThreadIsolation_UnscopedThreadNotAffected()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var childA = new Person { FirstName = "ChA" };
        var childB = new Person { FirstName = "ChB" };
        var parent = new Person(context) { FirstName = "Par", Mother = childA, Father = childB };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        childA.SetSubjectId("childAId");
        childB.SetSubjectId("childBId");

        // Act — scope on current thread, detach childB on separate thread
        using (lifecycle.CreateBatchScope(context))
        {
            // Detach childA on this thread (scoped — deferred)
            parent.Mother = null;

            // Detach childB on another thread (no scope — immediate)
            var thread = new Thread(() => parent.Father = null);
            thread.Start();
            thread.Join();

            // Assert — childA still in registry (deferred), childB gone (immediate)
            Assert.True(idRegistry.TryGetSubjectById("childAId", out _));
            Assert.False(idRegistry.TryGetSubjectById("childBId", out _));
        }

        // After scope dispose, childA is also removed
        Assert.False(idRegistry.TryGetSubjectById("childAId", out _));
    }
}
