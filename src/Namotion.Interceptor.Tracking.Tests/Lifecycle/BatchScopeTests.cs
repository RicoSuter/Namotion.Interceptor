using System.Collections;
using System.Reflection;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class BatchScopeTests
{
    [Fact]
    public void WhenLastReferenceIsRemovedInsideBatchScope_ThenDetachIsDeferredUntilDispose()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act: detach within batch scope
        using (lifecycle.CreateBatchScope(context))
        {
            parent.Mother = null;

            // Assert: still in registry during scope
            Assert.True(idRegistry.TryGetSubjectById("childId", out _));
        }

        // Assert: now removed (genuinely orphaned)
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void WhenSubjectMovesBetweenPropertiesInsideBatchScope_ThenItStaysRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act: move child from Mother to Father within batch scope
        using (lifecycle.CreateBatchScope(context))
        {
            parent.Mother = null;   // detach (deferred)
            parent.Father = child;  // reattach to different property
        }

        // Assert: child stays registered (was moved, not removed)
        Assert.True(idRegistry.TryGetSubjectById("childId", out var found));
        Assert.Same(child, found);
    }

    [Fact]
    public void WhenSubjectIsRemovedInsideBatchScope_ThenItIsCleanedUpOnDispose()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act: detach without reattach
        using (lifecycle.CreateBatchScope(context))
        {
            parent.Mother = null;
        }

        // Assert: genuinely orphaned, removed on dispose
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void WhenNoBatchScopeIsOpen_ThenDetachIsProcessedImmediately()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act: detach without any scope
        parent.Mother = null;

        // Assert: immediate removal
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void WhenBatchScopesAreNested_ThenDeferredDetachesAreProcessedOnOuterDispose()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        // Act: nested scopes
        var outer = lifecycle.CreateBatchScope(context);
        try
        {
            var inner = lifecycle.CreateBatchScope(context);
            try
            {
                parent.Mother = null;
            }
            finally
            {
                inner.Dispose();
            }

            // Assert: inner dispose does not process anything yet
            Assert.True(idRegistry.TryGetSubjectById("childId", out _));
        }
        finally
        {
            outer.Dispose();
        }

        // Assert: outer dispose processes the deferred detaches
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void WhenDetachRunsOnAnotherThreadDuringBatchScope_ThenThatThreadIsNotAffected()
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

        // Act: scope on current thread, detach childB on separate thread
        using (lifecycle.CreateBatchScope(context))
        {
            // Detach childA on this thread (scoped: deferred)
            parent.Mother = null;

            // Detach childB on another thread (no scope: immediate)
            var thread = new Thread(() => parent.Father = null);
            thread.Start();
            thread.Join();

            // Assert: childA still in registry (deferred), childB gone (immediate)
            Assert.True(idRegistry.TryGetSubjectById("childAId", out _));
            Assert.False(idRegistry.TryGetSubjectById("childBId", out _));
        }

        // Assert: after scope dispose, childA is also removed
        Assert.False(idRegistry.TryGetSubjectById("childAId", out _));
    }

    [Fact]
    public void WhenBatchScopeClosesWithoutDeferredDetaches_ThenNoThreadStaticStateRemains()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new Person(context) { FirstName = "Par" };
        var lifecycle = context.TryGetLifecycleInterceptor()!;

        // Act: a scope that only attaches, so nothing is ever deferred
        using (lifecycle.CreateBatchScope(context))
        {
            parent.Mother = new Person { FirstName = "Chi" };
        }

        // Assert: the scope pins neither the root context nor the owning interceptor
        AssertNoBatchScopeStateOnCurrentThread();
    }

    [Fact]
    public void WhenLifecycleHandlerThrowsOnBatchScopeClose_ThenNoStateIsStrandedAndTheNextScopeWorks()
    {
        // Arrange
        var throwingHandler = new ThrowingOnDetachLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithService<ILifecycleHandler>(
                () => throwingHandler,
                handler => handler is ThrowingOnDetachLifecycleHandler);

        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        var firstChild = new Person { FirstName = "Ch1" };
        var firstParent = new Person(context) { FirstName = "Pa1", Mother = firstChild };
        firstChild.SetSubjectId("firstChildId");

        var scope = lifecycle.CreateBatchScope(context);
        firstParent.Mother = null;

        // Act & Assert: the handler throws out of the scope close
        Assert.Throws<InvalidOperationException>(() => scope.Dispose());

        // Assert: the failed close left nothing behind on this thread
        AssertNoBatchScopeStateOnCurrentThread();

        // Arrange: a second, unrelated graph on the same thread
        throwingHandler.IsEnabled = false;
        var secondChild = new Person { FirstName = "Ch2" };
        var secondParent = new Person(context) { FirstName = "Pa2", Mother = secondChild };
        secondChild.SetSubjectId("secondChildId");

        // Act: a subsequent scope on the same thread
        using (lifecycle.CreateBatchScope(context))
        {
            secondParent.Mother = null;

            // Assert: the detach is deferred, not carried over from the failed scope
            Assert.True(idRegistry.TryGetSubjectById("secondChildId", out _));
        }

        // Assert: and it is processed normally on dispose
        Assert.False(idRegistry.TryGetSubjectById("secondChildId", out _));
        AssertNoBatchScopeStateOnCurrentThread();
    }

    [Fact]
    public void WhenTwoInterceptorsHaveOverlappingBatchScopes_ThenTheNonOwningDetachIsNotLost()
    {
        // Arrange: two independent graphs, so two LifecycleInterceptor instances
        var contextA = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var contextB = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

        var childA = new Person { FirstName = "ChA" };
        var parentA = new Person(contextA) { FirstName = "ParA", Mother = childA };
        var childB = new Person { FirstName = "ChB" };
        var parentB = new Person(contextB) { FirstName = "ParB", Mother = childB };

        childA.SetSubjectId("childAId");
        childB.SetSubjectId("childBId");

        var lifecycleA = contextA.TryGetLifecycleInterceptor()!;
        var lifecycleB = contextB.TryGetLifecycleInterceptor()!;
        var idRegistryA = contextA.GetService<ISubjectIdRegistry>();
        var idRegistryB = contextB.GetService<ISubjectIdRegistry>();

        // Act: B's scope overlaps A's, so A owns the batch and B must not defer
        using (lifecycleA.CreateBatchScope(contextA))
        {
            parentA.Mother = null;

            using (lifecycleB.CreateBatchScope(contextB))
            {
                parentB.Mother = null;

                // Assert: B detaches immediately because it does not own the batch
                Assert.False(idRegistryB.TryGetSubjectById("childBId", out _));
            }

            // Assert: closing B's scope neither processes nor drops A's deferred detach
            Assert.True(idRegistryA.TryGetSubjectById("childAId", out _));
        }

        // Assert: A's deferred detach is processed, B's stayed detached
        Assert.False(idRegistryA.TryGetSubjectById("childAId", out _));
        Assert.False(idRegistryB.TryGetSubjectById("childBId", out _));
        AssertNoBatchScopeStateOnCurrentThread();
    }

    [Fact]
    public void WhenBatchScopeIsDisposedOnAnotherThread_ThenItThrowsAndNeitherThreadIsCorrupted()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        var scope = lifecycle.CreateBatchScope(context);
        try
        {
            parent.Mother = null;

            // Act: dispose the scope from a foreign thread
            Exception? capturedException = null;
            var countOnOtherThreadBefore = -1;
            var countOnOtherThreadAfter = -1;
            var thread = new Thread(() =>
            {
                countOnOtherThreadBefore = GetBatchScopeCountOnCurrentThread();
                capturedException = Record.Exception(() => scope.Dispose());
                countOnOtherThreadAfter = GetBatchScopeCountOnCurrentThread();
            });

            thread.Start();
            thread.Join();

            // Assert: it throws and leaves the foreign thread untouched
            Assert.IsType<InvalidOperationException>(capturedException);
            Assert.Equal(0, countOnOtherThreadBefore);
            Assert.Equal(0, countOnOtherThreadAfter);

            // Assert: the owning thread still holds its open scope and its deferred detach
            Assert.Equal(1, GetBatchScopeCountOnCurrentThread());
            Assert.True(idRegistry.TryGetSubjectById("childId", out _));
        }
        finally
        {
            scope.Dispose();
        }

        // Assert: disposing on the owning thread still works
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
        AssertNoBatchScopeStateOnCurrentThread();
    }

    private static void AssertNoBatchScopeStateOnCurrentThread()
    {
        Assert.Equal(0, GetBatchScopeCountOnCurrentThread());
        Assert.Null(GetBatchScopeThreadStatic("s_batchScopeRootContext"));
        Assert.Null(GetBatchScopeThreadStatic("s_batchScopeOwner"));

        var deferred = (IDictionary?)GetBatchScopeThreadStatic("s_deferredLastDetaches");
        Assert.True(deferred is null or { Count: 0 }, "Deferred detaches are stranded on this thread.");
    }

    private static int GetBatchScopeCountOnCurrentThread()
    {
        return (int)GetBatchScopeThreadStatic("s_batchScopeCount")!;
    }

    private static object? GetBatchScopeThreadStatic(string fieldName)
    {
        var field = typeof(LifecycleInterceptor).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return field.GetValue(null);
    }

    private class ThrowingOnDetachLifecycleHandler : ILifecycleHandler
    {
        public bool IsEnabled { get; set; } = true;

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (IsEnabled && change.IsContextDetach)
            {
                throw new InvalidOperationException("Handler failure during detach.");
            }
        }
    }
}
