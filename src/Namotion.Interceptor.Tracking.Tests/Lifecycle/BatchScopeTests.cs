using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenSubjectMovesBetweenParentsInsideBatchScope_ThenItInheritsTheSameContextsAsWithoutTheScope(bool attachOnAnotherThread)
    {
        // Arrange: two identical graphs whose parents each carry their own context
        var scoped = CreateCrossParentGraph();
        var unscoped = CreateCrossParentGraph();
        var lifecycle = scoped.RootContext.TryGetLifecycleInterceptor()!;

        // Act: move the child across parents, once inside a batch scope and once without
        using (lifecycle.CreateBatchScope(scoped.RootContext))
        {
            scoped.FirstParent.Mother = null;
            if (attachOnAnotherThread)
            {
                var thread = new Thread(() => scoped.SecondParent.Mother = scoped.Child);
                thread.Start();
                thread.Join();
            }
            else
            {
                scoped.SecondParent.Mother = scoped.Child;
            }
        }

        unscoped.FirstParent.Mother = null;
        unscoped.SecondParent.Mother = unscoped.Child;

        // Assert: the child inherits from the new parent in both, not from the parent it left
        Assert.Equal(["SecondParent"], GetInheritedContextRoles(unscoped, unscoped.Child));
        Assert.Equal(
            GetInheritedContextRoles(unscoped, unscoped.Child),
            GetInheritedContextRoles(scoped, scoped.Child));
    }

    [Fact]
    public void WhenOldParentIsRemovedAfterABatchScopeMove_ThenTheMovedSubjectStillResolvesServices()
    {
        // Arrange
        var graph = CreateCrossParentGraph();
        var lifecycle = graph.RootContext.TryGetLifecycleInterceptor()!;
        var idRegistry = graph.RootContext.GetService<ISubjectIdRegistry>();

        using (lifecycle.CreateBatchScope(graph.RootContext))
        {
            graph.FirstParent.Mother = null;
            graph.SecondParent.Mother = graph.Child;
        }

        // Act: the parent the child left drops out of the graph, so its context resolves nothing
        graph.Root.Mother = null;

        // Assert: the child is still under the new parent and its context still reaches the graph services
        Assert.Same(graph.Child, graph.SecondParent.Mother);
        Assert.NotNull(((IInterceptorSubject)graph.Child).Context.TryGetService<ISubjectIdRegistry>());
        Assert.NotNull(graph.Child.TryGetRegisteredSubject());

        // Assert: a write to the child still runs the full interceptor chain
        graph.Child.SetSubjectId("childId");
        Assert.True(idRegistry.TryGetSubjectById("childId", out var found));
        Assert.Same(graph.Child, found);
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
    public void WhenDetachRunsOnAnotherThreadDuringBatchScope_ThenThatThreadDetachesImmediately()
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

        // Act
        using (lifecycle.CreateBatchScope(context))
        {
            parent.Mother = null;

            var thread = new Thread(() => parent.Father = null);
            thread.Start();
            thread.Join();

            // Assert: only the thread holding the scope defers its detaches
            Assert.True(idRegistry.TryGetSubjectById("childAId", out _));
            Assert.False(idRegistry.TryGetSubjectById("childBId", out _));
        }

        // Assert
        Assert.False(idRegistry.TryGetSubjectById("childAId", out _));
    }

    [Fact]
    public void WhenAnotherThreadHoldsAScopeOnTheSameGraph_ThenClosingThisThreadsScopeStillDetaches()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();
        child.SetSubjectId("childId");

        using var otherScopeOpened = new ManualResetEventSlim();
        using var releaseOtherScope = new ManualResetEventSlim();
        var otherThread = new Thread(() =>
        {
            using (lifecycle.CreateBatchScope(context))
            {
                otherScopeOpened.Set();
                releaseOtherScope.Wait();
            }
        });
        otherThread.Start();
        otherScopeOpened.Wait();

        try
        {
            // Act
            using (lifecycle.CreateBatchScope(context))
            {
                parent.Mother = null;
            }

            // Assert: the scope still open on the other thread does not keep this thread's detach deferred
            Assert.False(idRegistry.TryGetSubjectById("childId", out _));
        }
        finally
        {
            releaseOtherScope.Set();
            otherThread.Join();
        }
    }

    [Fact]
    public void WhenAnotherThreadDefersTheSameSubjectLater_ThenClosingThisThreadsScopeLeavesItForThatScope()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();
        child.SetSubjectId("childId");

        using var otherThreadDeferred = new ManualResetEventSlim();
        using var releaseOtherScope = new ManualResetEventSlim();
        var otherThread = new Thread(() =>
        {
            using (lifecycle.CreateBatchScope(context))
            {
                parent.Father = child;
                parent.Father = null;
                otherThreadDeferred.Set();
                releaseOtherScope.Wait();
            }
        });

        try
        {
            // Act: this thread defers the child first, then the other thread holds it and defers it again
            using (lifecycle.CreateBatchScope(context))
            {
                parent.Mother = null;
                otherThread.Start();
                otherThreadDeferred.Wait();
            }

            // Assert: the scope that removed the last reference most recently is still open
            Assert.True(idRegistry.TryGetSubjectById("childId", out _));
        }
        finally
        {
            releaseOtherScope.Set();
            otherThread.Join();
        }

        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenASubjectMovesToAnotherGraphWhileAScopeIsOpen_ThenItResolvesTheOtherGraph(bool onScopeThread)
    {
        // Arrange
        var contextA = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var contextB = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var rootA = new Person(contextA) { FirstName = "RootA", Mother = child };
        var rootB = new Person(contextB) { FirstName = "RootB" };
        var lifecycleA = contextA.TryGetLifecycleInterceptor()!;

        void Move()
        {
            rootA.Mother = null;
            rootB.Mother = child;
        }

        // Act
        using (lifecycleA.CreateBatchScope(contextA))
        {
            if (onScopeThread)
            {
                Move();
            }
            else
            {
                var thread = new Thread(Move);
                thread.Start();
                thread.Join();
            }
        }

        // Assert
        var registeredChild = child.TryGetRegisteredSubject();
        Assert.NotNull(registeredChild);
        Assert.Same(contextB.GetService<ISubjectRegistry>().TryGetRegisteredSubject(child), registeredChild);
        Assert.Same(rootB, registeredChild.Parents.Single().Property.Parent.Subject);
    }

    [Fact]
    public void WhenBatchScopeClosesWithoutDeferredDetaches_ThenTheRootContextIsNotRetained()
    {
        // Arrange & Act: a scope that only attaches, so nothing is ever deferred
        var weakContext = CreateContextAndCloseEmptyBatchScope();

        for (var i = 0; i < 10 && weakContext.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        // Assert: the closed scope pins neither the root context nor the graph and interceptor it reaches
        Assert.False(weakContext.IsAlive);
    }

    // NoInlining so the context local cannot be kept alive by the caller's frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateContextAndCloseEmptyBatchScope()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new Person(context) { FirstName = "Par" };
        var lifecycle = context.TryGetLifecycleInterceptor()!;

        using (lifecycle.CreateBatchScope(context))
        {
            parent.Mother = new Person { FirstName = "Chi" };
        }

        return new WeakReference(context);
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

        // Arrange: a second, unrelated graph
        throwingHandler.IsEnabled = false;
        var secondChild = new Person { FirstName = "Ch2" };
        var secondParent = new Person(context) { FirstName = "Pa2", Mother = secondChild };
        secondChild.SetSubjectId("secondChildId");

        // Act: a subsequent scope
        using (lifecycle.CreateBatchScope(context))
        {
            secondParent.Mother = null;

            // Assert: the detach is deferred, not carried over from the failed scope
            Assert.True(idRegistry.TryGetSubjectById("secondChildId", out _));
        }

        // Assert: and it is processed normally on dispose
        Assert.False(idRegistry.TryGetSubjectById("secondChildId", out _));
    }

    [Fact]
    public void WhenOneDeferredDetachThrows_ThenTheRemainingDetachesStillComplete()
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

        var failingChild = new Person { FirstName = "Ch1" };
        var survivingChild = new Person { FirstName = "Ch2" };
        var parent = new Person(context) { FirstName = "Par", Mother = failingChild, Father = survivingChild };

        failingChild.SetSubjectId("failingChildId");
        survivingChild.SetSubjectId("survivingChildId");

        throwingHandler.FailingSubject = failingChild;

        var scope = lifecycle.CreateBatchScope(context);
        parent.Mother = null;
        parent.Father = null;

        // Act & Assert: the handler failure surfaces unwrapped out of the scope close
        var exception = Assert.Throws<InvalidOperationException>(() => scope.Dispose());
        Assert.Equal("Handler failure during detach.", exception.Message);

        // Assert: the other deferred subject was still detached and deregistered
        Assert.False(idRegistry.TryGetSubjectById("survivingChildId", out _));

        // Act: re-attach the subject whose deferred detach ran after the failing one
        parent.Father = survivingChild;

        // Assert: it attaches as a new context attach, so it is not stuck in a
        // present-but-empty state where it can never be registered again
        Assert.True(idRegistry.TryGetSubjectById("survivingChildId", out var found));
        Assert.Same(survivingChild, found);
    }

    [Fact]
    public void WhenTwoInterceptorsHaveOverlappingBatchScopes_ThenEachDefersOnlyItsOwnGraph()
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

        // Act
        using (lifecycleA.CreateBatchScope(contextA))
        {
            parentA.Mother = null;

            using (lifecycleB.CreateBatchScope(contextB))
            {
                parentB.Mother = null;
            }

            // Assert: closing B's scope detaches B's subject and leaves A's deferred
            Assert.False(idRegistryB.TryGetSubjectById("childBId", out _));
            Assert.True(idRegistryA.TryGetSubjectById("childAId", out _));
        }

        // Assert
        Assert.False(idRegistryA.TryGetSubjectById("childAId", out _));
    }

    [Fact]
    public void WhenBatchScopeIsDisposedOnAnotherThread_ThenItCloses()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        var scope = lifecycle.CreateBatchScope(context);
        parent.Mother = null;

        // Act
        var thread = new Thread(scope.Dispose);
        thread.Start();
        thread.Join();

        // Assert
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void WhenBatchScopeIsDisposedTwice_ThenAnEnclosingScopeStaysOpen()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person { FirstName = "Chi" };
        var parent = new Person(context) { FirstName = "Par", Mother = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");

        using (lifecycle.CreateBatchScope(context))
        {
            var inner = lifecycle.CreateBatchScope(context);
            parent.Mother = null;

            // Act
            inner.Dispose();
            inner.Dispose();

            // Assert
            Assert.True(idRegistry.TryGetSubjectById("childId", out _));
        }

        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    /// <summary>
    /// A root graph with two parents which each carry their own context, as a connector receiver graph does,
    /// so that moving the child from one to the other actually changes the context it inherits.
    /// </summary>
    private static CrossParentGraph CreateCrossParentGraph()
    {
        var rootContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new Person(rootContext) { FirstName = "Roo" };
        var firstParent = new Person { FirstName = "Pa1" };
        var secondParent = new Person { FirstName = "Pa2" };
        var child = new Person { FirstName = "Chi" };

        root.Mother = firstParent;
        root.Father = secondParent;
        firstParent.Mother = child;

        return new CrossParentGraph
        {
            RootContext = rootContext,
            Root = root,
            FirstParent = firstParent,
            SecondParent = secondParent,
            Child = child
        };
    }

    /// <summary>
    /// The subject's fallback contexts, named by the graph position each one belongs to, so that the chains
    /// of two structurally identical graphs can be compared directly.
    /// </summary>
    private static string[] GetInheritedContextRoles(CrossParentGraph graph, IInterceptorSubject subject)
    {
        var stateField = typeof(InterceptorSubjectContext)
            .GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(stateField);

        var state = stateField.GetValue(subject.Context);
        Assert.NotNull(state);

        var fallbackContextsField = state.GetType()
            .GetField("FallbackContexts", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(fallbackContextsField);

        var fallbackContexts = (IEnumerable)fallbackContextsField.GetValue(state)!;
        return fallbackContexts.Cast<object>().Select(graph.GetRole).ToArray();
    }

    private sealed class CrossParentGraph
    {
        public required IInterceptorSubjectContext RootContext { get; init; }

        public required Person Root { get; init; }

        public required Person FirstParent { get; init; }

        public required Person SecondParent { get; init; }

        public required Person Child { get; init; }

        public string GetRole(object context)
        {
            if (ReferenceEquals(context, RootContext))
                return "RootContext";

            if (ReferenceEquals(context, ((IInterceptorSubject)Root).Context))
                return "Root";

            if (ReferenceEquals(context, ((IInterceptorSubject)FirstParent).Context))
                return "FirstParent";

            if (ReferenceEquals(context, ((IInterceptorSubject)SecondParent).Context))
                return "SecondParent";

            if (ReferenceEquals(context, ((IInterceptorSubject)Child).Context))
                return "Child";

            return "Unknown";
        }
    }

    private class ThrowingOnDetachLifecycleHandler : ILifecycleHandler
    {
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// The only subject to fail for, or null to fail for every context detach.
        /// </summary>
        public IInterceptorSubject? FailingSubject { get; set; }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (IsEnabled && change.IsContextDetach &&
                (FailingSubject is null || ReferenceEquals(FailingSubject, change.Subject)))
            {
                throw new InvalidOperationException("Handler failure during detach.");
            }
        }
    }
}
