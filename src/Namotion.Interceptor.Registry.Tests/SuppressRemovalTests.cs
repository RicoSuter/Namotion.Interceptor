using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

public class SuppressRemovalTests
{
    [Fact]
    public void BasicSuppression_DetachDeferredThenProcessedOnResume()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var idRegistry = context.GetService<ISubjectIdRegistry>();
        var registry = (SubjectRegistry)context.GetService<ISubjectRegistry>();

        child.SetSubjectId("childId");
        Assert.True(idRegistry.TryGetSubjectById("childId", out _));

        // Act — detach within suppression scope
        var scope = registry.SuppressRemoval();
        parent.Mother = null;

        // Assert — still in registry maps during scope
        Assert.True(idRegistry.TryGetSubjectById("childId", out _));
        Assert.NotNull(registry.TryGetRegisteredSubject((IInterceptorSubject)child));

        // Act — dispose scope
        scope.Dispose();

        // Assert — now removed
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
        Assert.Null(registry.TryGetRegisteredSubject((IInterceptorSubject)child));
    }

    [Fact]
    public void MoveBetweenProperties_SubjectStaysRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var registry = (SubjectRegistry)context.GetService<ISubjectRegistry>();
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — move child from Mother to Father within suppression scope
        using (registry.SuppressRemoval())
        {
            parent.Mother = null;   // detach
            parent.Father = child;  // reattach to different property
        }

        // Assert — child stays registered (was moved, not removed)
        Assert.True(idRegistry.TryGetSubjectById("childId", out var found));
        Assert.Same(child, found);
        Assert.NotNull(registry.TryGetRegisteredSubject((IInterceptorSubject)child));
    }

    [Fact]
    public void GenuineRemoval_RemovedOnResume()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var registry = (SubjectRegistry)context.GetService<ISubjectRegistry>();
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — detach without reattach
        using (registry.SuppressRemoval())
        {
            parent.Mother = null;
        }

        // Assert — genuinely orphaned, removed on resume
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
        Assert.Null(registry.TryGetRegisteredSubject((IInterceptorSubject)child));
    }

    [Fact]
    public void NoScope_ExistingBehaviorUnchanged()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var idRegistry = context.GetService<ISubjectIdRegistry>();
        var registry = (SubjectRegistry)context.GetService<ISubjectRegistry>();

        child.SetSubjectId("childId");

        // Act — detach without any scope
        parent.Mother = null;

        // Assert — immediate removal (existing behavior)
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
        Assert.Null(registry.TryGetRegisteredSubject((IInterceptorSubject)child));
    }

    [Fact]
    public void NestedScopes_ProcessedOnOuterDispose()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var registry = (SubjectRegistry)context.GetService<ISubjectRegistry>();
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — nested scopes
        var outer = registry.SuppressRemoval();
        var inner = registry.SuppressRemoval();

        parent.Mother = null;

        // Dispose inner — no processing yet
        inner.Dispose();
        Assert.True(idRegistry.TryGetSubjectById("childId", out _));

        // Dispose outer — processes deferred removals
        outer.Dispose();
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void ThreadIsolation_UnscopedThreadNotAffected()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var childA = new Models.Person { FirstName = "ChildA" };
        var childB = new Models.Person { FirstName = "ChildB" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = childA, Father = childB };
        var registry = (SubjectRegistry)context.GetService<ISubjectRegistry>();
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        childA.SetSubjectId("childAId");
        childB.SetSubjectId("childBId");

        // Act — scope on current thread, detach childB on separate thread
        using (registry.SuppressRemoval())
        {
            // Detach childA on this thread (scoped — deferred)
            parent.Mother = null;

            // Detach childB on another thread (no scope — immediate)
            var thread = new Thread(() => parent.Father = null);
            thread.Start();
            thread.Join();

            // Assert — childA still in registry (deferred), childB already gone (immediate)
            Assert.True(idRegistry.TryGetSubjectById("childAId", out _));
            Assert.False(idRegistry.TryGetSubjectById("childBId", out _));
        }

        // After scope dispose, childA is also removed
        Assert.False(idRegistry.TryGetSubjectById("childAId", out _));
    }

    [Fact]
    public void CrossThreadReattach_SkipsRemoval()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var registry = (SubjectRegistry)context.GetService<ISubjectRegistry>();
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act — detach on this thread (scoped), reattach on another thread
        using (registry.SuppressRemoval())
        {
            parent.Mother = null;

            // Reattach on another thread
            var thread = new Thread(() => parent.Father = child);
            thread.Start();
            thread.Join();
        }

        // Assert — child still registered (has parents from thread B)
        Assert.True(idRegistry.TryGetSubjectById("childId", out var found));
        Assert.Same(child, found);
    }
}
