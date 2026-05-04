using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Tests that detached subjects are properly released from the parent context's
/// _usedByContexts set, allowing GC to collect them.
/// </summary>
public class ContextRetentionLeakTests
{
    /// <summary>
    /// Basic attach/detach cycle: create child, attach via ObjectRef, detach.
    /// After GC, the child should be collected (not retained by parent context).
    /// </summary>
    [Fact]
    public void WhenChildDetached_ThenChildIsGarbageCollected()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var root = new Person(context) { FirstName = "Root" };
        var weakRefs = new List<WeakReference>();

        // Act: attach and detach 100 children
        for (var i = 0; i < 100; i++)
        {
            var child = new Person { FirstName = $"Child_{i}" };
            root.Mother = child;
            root.Mother = null;
            weakRefs.Add(new WeakReference(child));
        }

        // Force GC
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert: all children should be collected
        var alive = weakRefs.Count(w => w.IsAlive);
        // Allow 1 — the last loop variable may still be on the stack frame (Debug builds don't clear dead locals)
        Assert.True(alive <= 1, $"{alive} of 100 detached children are still alive after GC — likely retained by parent context._usedByContexts");
    }

    /// <summary>
    /// Same test but with collection property (Children array).
    /// </summary>
    [Fact]
    public void WhenCollectionChildDetached_ThenChildIsGarbageCollected()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var root = new Person(context) { FirstName = "Root" };
        var weakRefs = new List<WeakReference>();

        // Act: add and remove children from collection
        for (var i = 0; i < 100; i++)
        {
            var child = new Person { FirstName = $"Child_{i}" };
            root.Children = [child];
            root.Children = [];
            weakRefs.Add(new WeakReference(child));
        }

        // Force GC
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert
        var alive = weakRefs.Count(w => w.IsAlive);
        Assert.True(alive <= 1, $"{alive} of 100 detached collection children are still alive after GC");
    }

    /// <summary>
    /// Deep graph: root → child → grandchild. Detach child (should cascade to grandchild).
    /// Both child and grandchild should be GC'd.
    /// </summary>
    [Fact]
    public void WhenDeepGraphDetached_ThenAllDescendantsAreGarbageCollected()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var root = new Person(context) { FirstName = "Root" };
        var weakRefs = new List<WeakReference>();

        // Act
        for (var i = 0; i < 50; i++)
        {
            var grandchild = new Person { FirstName = $"Grandchild_{i}" };
            var child = new Person { FirstName = $"Child_{i}", Mother = grandchild };
            root.Mother = child;
            root.Mother = null;
            weakRefs.Add(new WeakReference(child));
            weakRefs.Add(new WeakReference(grandchild));
        }

        // Force GC
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert
        var alive = weakRefs.Count(w => w.IsAlive);
        // Allow 2 — last child + grandchild from the final loop iteration may still be on the stack
        Assert.True(alive <= 2, $"{alive} of 100 detached descendants are still alive after GC");
    }

    /// <summary>
    /// Same as basic test but WITH registry — closer to tester setup.
    /// </summary>
    [Fact]
    public void WhenChildDetachedWithRegistry_ThenMemoryDoesNotGrow()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };

        // Warm up
        for (var i = 0; i < 100; i++)
        {
            var child = new Person { FirstName = $"Warmup_{i}" };
            root.Mother = child;
            root.Mother = null;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

        // Act: 1000 attach/detach cycles with registry
        for (var i = 0; i < 1000; i++)
        {
            var child = new Person { FirstName = $"Child_{i}" };
            root.Mother = child;
            root.Mother = null;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var afterBytes = GC.GetTotalMemory(forceFullCollection: true);

        var growthKb = (afterBytes - baselineBytes) / 1024.0;
        Assert.True(growthKb < 50, $"Memory grew by {growthKb:F1} KB after 1000 attach/detach cycles WITH registry — likely a retention leak");
    }

    /// <summary>
    /// Deep graph with registry — child has pre-populated structural property (like applier Phase 1).
    /// </summary>
    [Fact]
    public void WhenDeepGraphWithRegistryDetached_ThenMemoryDoesNotGrow()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };

        // Warm up
        for (var i = 0; i < 50; i++)
        {
            var grandchild = new Person { FirstName = $"WarmupGC_{i}" };
            var child = new Person { FirstName = $"Warmup_{i}", Mother = grandchild };
            root.Mother = child;
            root.Mother = null;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

        // Act: 500 deep attach/detach cycles
        for (var i = 0; i < 500; i++)
        {
            var grandchild = new Person { FirstName = $"Grandchild_{i}" };
            var child = new Person { FirstName = $"Child_{i}", Mother = grandchild };
            root.Mother = child;
            root.Mother = null;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var afterBytes = GC.GetTotalMemory(forceFullCollection: true);

        var growthKb = (afterBytes - baselineBytes) / 1024.0;
        Assert.True(growthKb < 100, $"Memory grew by {growthKb:F1} KB after 500 deep attach/detach cycles WITH registry");
    }

    /// <summary>
    /// Sustained mutation loop simulating the tester: attach/detach many subjects over time.
    /// After all detached and GC'd, only root should remain.
    /// </summary>
    [Fact]
    public void WhenManySubjectsAttachedAndDetached_ThenMemoryDoesNotGrow()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var root = new Person(context) { FirstName = "Root" };

        // Warm up
        for (var i = 0; i < 100; i++)
        {
            root.Mother = new Person { FirstName = $"Warmup_{i}" };
            root.Mother = null;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

        // Act: 1000 attach/detach cycles
        for (var i = 0; i < 1000; i++)
        {
            var child = new Person { FirstName = $"Child_{i}" };
            root.Mother = child;
            root.Mother = null;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var afterBytes = GC.GetTotalMemory(forceFullCollection: true);

        // Assert: should not grow more than ~50KB (tolerance for dict capacity etc.)
        var growthKb = (afterBytes - baselineBytes) / 1024.0;
        Assert.True(growthKb < 50, $"Memory grew by {growthKb:F1} KB after 1000 attach/detach cycles — likely a retention leak");
    }

    /// <summary>
    /// Exercises the SubjectUpdateApplier path (CreateCompleteUpdate + ApplySubjectUpdate)
    /// which uses batch scope and the update serialization pipeline.
    /// </summary>
    [Fact]
    public void WhenApplierCreatesAndDestroysSubjects_ThenMemoryDoesNotGrow()
    {
        // Arrange: two participants (simulates server + client)
        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var clientContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

        var serverRoot = new Person(serverContext) { FirstName = "ServerRoot" };
        var clientRoot = new Person(clientContext) { FirstName = "ClientRoot" };

        // Warm up: a few complete update round-trips
        for (var i = 0; i < 20; i++)
        {
            serverRoot.Mother = new Person { FirstName = $"Warmup_{i}", Mother = new Person { FirstName = $"WarmupGC_{i}" } };
            var update = Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
            clientRoot.ApplySubjectUpdate(update, null);
            serverRoot.Mother = null;
            update = Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
            clientRoot.ApplySubjectUpdate(update, null);
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

        // Act: 2000 apply cycles (create → complete update → apply → remove → complete update → apply)
        for (var i = 0; i < 2000; i++)
        {
            // Server adds a child with grandchild
            serverRoot.Mother = new Person { FirstName = $"Child_{i}", Mother = new Person { FirstName = $"GC_{i}" } };

            // Create snapshot and apply to client
            var addUpdate = Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
            clientRoot.ApplySubjectUpdate(addUpdate, null);

            // Server removes the child
            serverRoot.Mother = null;

            // Create snapshot and apply to client
            var removeUpdate = Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
            clientRoot.ApplySubjectUpdate(removeUpdate, null);
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var afterBytes = GC.GetTotalMemory(forceFullCollection: true);

        var growthKb = (afterBytes - baselineBytes) / 1024.0;
        Assert.True(growthKb < 200, $"Memory grew by {growthKb:F1} KB after 2000 applier round-trips — likely a retention leak in the update pipeline");
    }

    /// <summary>
    /// Tests that SourceOwnershipManager._properties doesn't accumulate entries
    /// for detached subjects. This simulates the WebSocket client pattern where
    /// SubjectAttached claims ownership and SubjectDetaching releases it.
    /// </summary>
    [Fact]
    public void WhenSourceOwnershipUsed_ThenDetachedSubjectPropertiesAreReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new Person(context) { FirstName = "Root" };

        // Simulate WebSocket source ownership pattern
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var mockSource = new Moq.Mock<Connectors.ISubjectSource>();
        mockSource.Setup(s => s.RootSubject).Returns(root);
        var ownership = new Connectors.SourceOwnershipManager(mockSource.Object);

        // Subscribe to SubjectAttached to claim ownership (like WebSocketSubjectClientSource)
        lifecycle.SubjectAttached += change =>
        {
            var registered = change.Subject.TryGetRegisteredSubject();
            if (registered is null) return;
            foreach (var prop in registered.Properties)
            {
                ownership.ClaimSource(prop.Reference);
            }
        };

        // Warm up
        for (var i = 0; i < 10; i++)
        {
            root.Mother = new Person { FirstName = $"Warmup_{i}" };
            root.Mother = null;
        }

        var initialCount = ownership.Properties.Count;

        // Act: 500 attach/detach cycles
        for (var i = 0; i < 500; i++)
        {
            var child = new Person { FirstName = $"Child_{i}" };
            root.Mother = child;
            root.Mother = null;
        }

        var finalCount = ownership.Properties.Count;

        // Assert: property count should be stable (not growing)
        Assert.True(finalCount <= initialCount + 10,
            $"SourceOwnershipManager._properties grew from {initialCount} to {finalCount} after 500 attach/detach cycles — SubjectDetaching not cleaning up");

        ownership.Dispose();
    }

    /// <summary>
    /// Same as above but using ApplySubjectUpdate (batch scope) path.
    /// </summary>
    [Fact]
    public void WhenApplierWithSourceOwnership_ThenPropertiesAreReleased()
    {
        // Arrange: server creates subjects, client applies via update
        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var clientContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

        var serverRoot = new Person(serverContext) { FirstName = "ServerRoot" };
        var clientRoot = new Person(clientContext) { FirstName = "ClientRoot" };

        var lifecycle = clientContext.TryGetLifecycleInterceptor()!;
        var mockSource = new Moq.Mock<Connectors.ISubjectSource>();
        mockSource.Setup(s => s.RootSubject).Returns(clientRoot);
        var ownership = new Connectors.SourceOwnershipManager(mockSource.Object);

        lifecycle.SubjectAttached += change =>
        {
            var registered = change.Subject.TryGetRegisteredSubject();
            if (registered is null) return;
            foreach (var prop in registered.Properties)
            {
                ownership.ClaimSource(prop.Reference);
            }
        };

        // Warm up
        for (var i = 0; i < 10; i++)
        {
            serverRoot.Mother = new Person { FirstName = $"Warmup_{i}" };
            var addUpdate = Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
            clientRoot.ApplySubjectUpdate(addUpdate, null);
            serverRoot.Mother = null;
            var removeUpdate = Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
            clientRoot.ApplySubjectUpdate(removeUpdate, null);
        }

        var initialCount = ownership.Properties.Count;

        // Act: 200 apply cycles via update pipeline (uses batch scope)
        for (var i = 0; i < 200; i++)
        {
            serverRoot.Mother = new Person { FirstName = $"Child_{i}" };
            var addUpdate = Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
            clientRoot.ApplySubjectUpdate(addUpdate, null);

            serverRoot.Mother = null;
            var removeUpdate = Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
            clientRoot.ApplySubjectUpdate(removeUpdate, null);
        }

        var finalCount = ownership.Properties.Count;

        Assert.True(finalCount <= initialCount + 10,
            $"SourceOwnershipManager._properties grew from {initialCount} to {finalCount} after 200 applier round-trips — SubjectDetaching not firing during batch scope");

        ownership.Dispose();
    }

    /// <summary>
    /// Concurrent mutations during applier (batch scope) — simulates the tester.
    /// One thread applies server updates, another mutates the graph.
    /// </summary>
    [Fact]
    public void WhenConcurrentMutationsDuringApply_ThenOwnershipPropertiesDoNotLeak()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var clientContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

        var serverRoot = new Person(serverContext) { FirstName = "ServerRoot" };
        var clientRoot = new Person(clientContext) { FirstName = "ClientRoot" };

        var lifecycle = clientContext.TryGetLifecycleInterceptor()!;
        var mockSource = new Moq.Mock<Connectors.ISubjectSource>();
        mockSource.Setup(s => s.RootSubject).Returns(clientRoot);
        var ownership = new Connectors.SourceOwnershipManager(mockSource.Object);

        lifecycle.SubjectAttached += change =>
        {
            var registered = change.Subject.TryGetRegisteredSubject();
            if (registered is null) return;
            foreach (var prop in registered.Properties)
            {
                ownership.ClaimSource(prop.Reference);
            }
        };

        // Warm up
        serverRoot.Mother = new Person { FirstName = "Init" };
        var initUpdate = Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
        clientRoot.ApplySubjectUpdate(initUpdate, null);
        serverRoot.Mother = null;
        initUpdate = Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
        clientRoot.ApplySubjectUpdate(initUpdate, null);

        var initialCount = ownership.Properties.Count;

        // Act: concurrent mutations + applier
        var cts = new CancellationTokenSource();
        var mutationThread = new Thread(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    clientRoot.Mother = new Person { FirstName = "Mutation" };
                    clientRoot.Mother = null;
                }
                catch { /* ignore races */ }
            }
        }) { IsBackground = true };

        mutationThread.Start();

        for (var i = 0; i < 200; i++)
        {
            serverRoot.Mother = new Person { FirstName = $"Server_{i}" };
            var addUpdate = Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
            clientRoot.ApplySubjectUpdate(addUpdate, null);

            serverRoot.Mother = null;
            var removeUpdate = Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
            clientRoot.ApplySubjectUpdate(removeUpdate, null);
        }

        cts.Cancel();
        mutationThread.Join();

        // Clean up
        clientRoot.Mother = null;

        var finalCount = ownership.Properties.Count;

        Assert.True(finalCount <= initialCount + 50,
            $"SourceOwnershipManager._properties grew from {initialCount} to {finalCount} after 200 concurrent applier cycles — leak in SubjectDetaching during batch scope");

        ownership.Dispose();
    }

    /// <summary>
    /// Tests that depth-2 subjects (grandchildren with pre-populated properties)
    /// are GC'd after detach. Simulates the tester's TestNode.CreateWithGraph pattern.
    /// </summary>
    [Fact]
    public void WhenDeepGraphWithPrePopulatedChildren_ThenAllAreGarbageCollected()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new Person(context) { FirstName = "Root" };
        var weakRefs = new List<WeakReference>();

        // Act: create depth-2 graphs like CreateWithGraph does:
        // child has pre-populated Children (set before entering graph)
        for (var i = 0; i < 50; i++)
        {
            // Depth-2 nodes (no context)
            var grandchild1 = new Person { FirstName = $"GC1_{i}" };
            var grandchild2 = new Person { FirstName = $"GC2_{i}" };

            // Depth-1 node with pre-populated Collection (no context)
            var child = new Person
            {
                FirstName = $"Child_{i}",
                Children = [grandchild1, grandchild2],
                Mother = new Person { FirstName = $"GCRef_{i}" }
            };

            // Enter graph (like CreateWithGraph)
            root.Children = [child];
            // Exit graph
            root.Children = [];

            weakRefs.Add(new WeakReference(child));
            weakRefs.Add(new WeakReference(grandchild1));
            weakRefs.Add(new WeakReference(grandchild2));
        }

        // Force GC
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert
        var alive = weakRefs.Count(w => w.IsAlive);
        Assert.True(alive <= 3,
            $"{alive} of {weakRefs.Count} detached depth-2 subjects are still alive after GC — " +
            "likely retained by parent executor's _usedByContexts or _fallbackContexts");
    }

    /// <summary>
    /// Deep graph through the APPLIER path (batch scope) — closest to the tester flow.
    /// </summary>
    [Fact]
    public void WhenDeepGraphAppliedAndRemoved_ThenAllAreGarbageCollected()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var clientContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var serverRoot = new Person(serverContext) { FirstName = "ServerRoot" };
        var clientRoot = new Person(clientContext) { FirstName = "ClientRoot" };
        var weakRefs = new List<WeakReference>();

        // Warm up
        for (var i = 0; i < 5; i++)
        {
            serverRoot.Children = [new Person { FirstName = $"W_{i}", Children = [new Person()] }];
            clientRoot.ApplySubjectUpdate(Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []), null);
            serverRoot.Children = [];
            clientRoot.ApplySubjectUpdate(Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []), null);
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Act: 100 deep graph apply/remove cycles
        for (var i = 0; i < 100; i++)
        {
            serverRoot.Children = [new Person { FirstName = $"C_{i}", Children = [new Person()], Mother = new Person() }];
            clientRoot.ApplySubjectUpdate(Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []), null);

            // Track client-side instances
            var registry = clientContext.GetService<Namotion.Interceptor.Registry.Abstractions.ISubjectRegistry>();
            foreach (var kvp in registry.KnownSubjects)
            {
                if (!ReferenceEquals(kvp.Key, clientRoot))
                    weakRefs.Add(new WeakReference(kvp.Key));
            }

            serverRoot.Children = [];
            clientRoot.ApplySubjectUpdate(Connectors.Updates.SubjectUpdate.CreateCompleteUpdate(serverRoot, []), null);
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        var alive = weakRefs.Count(w => w.IsAlive);
        Assert.True(alive <= 5, $"{alive} of {weakRefs.Count} applier-created deep subjects still alive after GC");
    }
}
