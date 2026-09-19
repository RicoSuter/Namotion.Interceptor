using Xunit;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.ConnectorTester.Snapshot;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

public class StructuralMutatorTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle();

    private static IInterceptorSubjectContext CreateTransactionalContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle()
            .WithTransactions();

    /// <summary>Every distinct node reachable from the root, following all three edge kinds.</summary>
    private static List<TestNode> ReachableNodes(TestNode root)
    {
        var visited = new HashSet<TestNode>();
        var pending = new Stack<TestNode>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!visited.Add(node))
            {
                continue;
            }

            foreach (var child in node.Collection)
            {
                pending.Push(child);
            }

            foreach (var child in node.Items.Values)
            {
                pending.Push(child);
            }

            if (node.ObjectRef is { } objectRef)
            {
                pending.Push(objectRef);
            }
        }

        return visited.ToList();
    }

    private static int ChildCount(TestNode node) => node.Collection.Length + node.Items.Count;

    /// <summary>Whether both sequences hold the same instances, ignoring order.</summary>
    private static bool SameMembership(IEnumerable<TestNode> left, IEnumerable<TestNode> right)
    {
        var leftSet = new HashSet<TestNode>(left);
        var rightSet = new HashSet<TestNode>(right);
        return leftSet.SetEquals(rightSet);
    }

    private static bool HoldsChild(TestNode parent, TestNode child)
        => parent.Collection.Any(entry => ReferenceEquals(entry, child))
            || parent.Items.Values.Any(entry => ReferenceEquals(entry, child));

    /// <summary>
    /// Asserts that no container holds the same instance twice, which is the one shape the
    /// mutator must never produce even though the model may otherwise be a DAG.
    /// </summary>
    private static void AssertNoContainerHoldsANodeTwice(TestNode root)
    {
        foreach (var node in ReachableNodes(root))
        {
            var collection = node.Collection;
            Assert.Equal(collection.Length, collection.Distinct().Count());

            var items = node.Items.Values.ToList();
            Assert.Equal(items.Count, items.Distinct().Count());
        }
    }

    [Fact]
    public void WhenStructuralTargetsEmpty_ThenPerformMutationReturnsWithoutThrowing()
    {
        // Arrange
        var graph = new KnownNodeGraph();
        var mutator = new StructuralMutator(graph);

        // Act / Assert (no exception)
        mutator.PerformMutation();
    }

    [Fact]
    public void WhenInvokedManyTimes_ThenGraphMutationsStayWithinMinAndMaxBounds()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestNode(context)
        {
            Collection = [new TestNode(), new TestNode(), new TestNode()],
            Items = new Dictionary<string, TestNode> { ["item-0"] = new() }
        };
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var mutator = new StructuralMutator(graph);

        // Act: drive many mutations.
        for (var i = 0; i < 200; i++)
        {
            mutator.PerformMutation();
            graph.Rebuild(root);
        }

        // Assert: collection bounded by [0, 30] (the per-node max), aggregate node count bounded by MaxTotalNodes.
        Assert.True(graph.KnownNodes.Count <= 500);
    }

    /// <summary>
    /// A three-level tree plus an empty node hanging off the root's object reference, returned as
    /// <paramref name="destination"/>. Every structural target other than the destination holds
    /// container children, so a move always finds an eligible source, and the destination is never
    /// itself a candidate child because object references are never picked as a move source.
    /// </summary>
    private static TestNode BuildMoveGraph(IInterceptorSubjectContext context, out TestNode destination)
    {
        destination = new TestNode(context);

        var branches = Enumerable.Range(0, 3)
            .Select(_ => new TestNode(context)
            {
                Collection = [new TestNode(context) { Collection = [new TestNode(context)] }]
            })
            .ToArray();

        return new TestNode(context)
        {
            Collection = branches,
            ObjectRef = destination
        };
    }

    /// <summary>Maps every node reachable from the root to the container parents that hold it.</summary>
    private static Dictionary<TestNode, List<TestNode>> BuildParentMap(TestNode root)
    {
        var parents = new Dictionary<TestNode, List<TestNode>>();

        foreach (var node in ReachableNodes(root))
        {
            foreach (var child in node.Collection.Concat(node.Items.Values))
            {
                if (!parents.TryGetValue(child, out var list))
                {
                    list = [];
                    parents[child] = list;
                }

                list.Add(node);
            }
        }

        return parents;
    }

    [Fact]
    public void WhenCrossParentMoveApplied_ThenTheSameInstanceLeavesItsParentAndJoinsTheDestination()
    {
        // Arrange
        var context = CreateContext();
        var root = BuildMoveGraph(context, out var destination);
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var mutator = new StructuralMutator(graph);
        var nodesBefore = ReachableNodes(root).Count;
        var parentsBefore = BuildParentMap(root);

        // Act
        var applied = mutator.PerformMutation(StructuralMutationKind.CrossParentMove, destination);

        // Assert
        Assert.True(applied);
        Assert.Equal(1, ChildCount(destination));

        var moved = destination.Collection.Concat(destination.Items.Values).Single();
        var formerParent = Assert.Single(parentsBefore[moved]);
        Assert.False(HoldsChild(formerParent, moved));

        // The move must reuse the instance rather than clone it, so the node count is unchanged.
        Assert.Equal(nodesBefore, ReachableNodes(root).Count);
        AssertNoContainerHoldsANodeTwice(root);
    }

    [Fact]
    public async Task WhenCrossParentMoveRunsOnATransactionalContext_ThenTheMoveStillRelocatesOneInstance()
    {
        // Arrange: with transaction support the detach and the attach commit together, so they
        // reach connectors as a single structural update.
        var context = CreateTransactionalContext();
        var root = BuildMoveGraph(context, out var destination);
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var mutator = new StructuralMutator(graph, context);
        var nodesBefore = ReachableNodes(root).Count;
        var parentsBefore = BuildParentMap(root);

        // Act
        var applied = await mutator.PerformMutationAsync(StructuralMutationKind.CrossParentMove, destination);

        // Assert
        Assert.True(applied);
        Assert.Equal(1, ChildCount(destination));

        var moved = destination.Collection.Concat(destination.Items.Values).Single();
        var formerParent = Assert.Single(parentsBefore[moved]);
        Assert.False(HoldsChild(formerParent, moved));
        Assert.Equal(nodesBefore, ReachableNodes(root).Count);
        AssertNoContainerHoldsANodeTwice(root);
    }

    [Fact]
    public void WhenReAddApplied_ThenTheSameInstancesRemainUnderTheParent()
    {
        // Arrange
        var context = CreateContext();
        var childA = new TestNode(context);
        var childB = new TestNode(context);
        var childC = new TestNode(context);
        var root = new TestNode(context) { Collection = [childA, childB, childC] };
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var mutator = new StructuralMutator(graph);

        // Act
        var applied = mutator.PerformMutation(StructuralMutationKind.ReAdd, root);

        // Assert: same three instances, still exactly one entry each, no new node created.
        Assert.True(applied);
        Assert.Equal(3, ChildCount(root));
        Assert.True(HoldsChild(root, childA));
        Assert.True(HoldsChild(root, childB));
        Assert.True(HoldsChild(root, childC));
        Assert.Equal(4, ReachableNodes(root).Count);
        AssertNoContainerHoldsANodeTwice(root);
    }

    [Fact]
    public void WhenReorderApplied_ThenCollectionMembershipIsUnchangedAndOrderDiffers()
    {
        // Arrange
        var context = CreateContext();
        var children = Enumerable.Range(0, 6).Select(_ => new TestNode(context)).ToArray();
        var root = new TestNode(context) { Collection = children };
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var mutator = new StructuralMutator(graph);

        // Act
        var applied = mutator.PerformMutation(StructuralMutationKind.Reorder, root);

        // Assert
        Assert.True(applied);
        Assert.Equal(children.Length, root.Collection.Length);
        Assert.True(SameMembership(children, root.Collection));
        Assert.False(root.Collection.SequenceEqual(children));
        AssertNoContainerHoldsANodeTwice(root);
    }

    [Fact]
    public void WhenReorderTargetHasFewerThanTwoEntries_ThenNothingIsApplied()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestNode(context) { Collection = [new TestNode()] };
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var mutator = new StructuralMutator(graph);

        // Act
        var applied = mutator.PerformMutation(StructuralMutationKind.Reorder, root);

        // Assert
        Assert.False(applied);
        Assert.Single(root.Collection);
    }

    [Fact]
    public void WhenSharedReferenceApplied_ThenTwoParentsReferenceTheSameInstance()
    {
        // Arrange: root -> Collection -> holder -> Collection -> shared. The only pickable source
        // edge is holder -> shared, so the mutation must point root's object reference at it.
        var context = CreateContext();
        var shared = new TestNode(context);
        var holder = new TestNode(context) { Collection = [shared] };
        var root = new TestNode(context) { Collection = [holder] };
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var mutator = new StructuralMutator(graph);

        // Act
        var applied = mutator.PerformMutation(StructuralMutationKind.SharedReference, root);

        // Assert: the node now has two parents and is still a single instance.
        Assert.True(applied);
        Assert.Same(shared, root.ObjectRef);
        Assert.True(HoldsChild(holder, shared));
        Assert.Equal(3, ReachableNodes(root).Count);
        AssertNoContainerHoldsANodeTwice(root);
    }

    [Fact]
    public void WhenSharedReferenceWouldCloseACycle_ThenNothingIsApplied()
    {
        // Arrange: the only candidate child is "holder", and root is reachable from holder,
        // so pointing holder's parent chain back at root would close a cycle.
        var context = CreateContext();
        var root = new TestNode(context);
        var holder = new TestNode(context) { ObjectRef = root };
        root.Collection = [holder];
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var mutator = new StructuralMutator(graph);

        // Act: holder is the only container child in the graph, and it can already reach root.
        var applied = mutator.PerformMutation(StructuralMutationKind.SharedReference, holder);

        // Assert
        Assert.False(applied);
    }

    [Fact]
    public void WhenCanReachWalksADag_ThenItTerminatesAndReportsReachability()
    {
        // Arrange: shared is reachable from root by two paths; unrelated is reachable from neither.
        var context = CreateContext();
        var shared = new TestNode(context);
        var holder = new TestNode(context) { Collection = [shared] };
        var root = new TestNode(context) { Collection = [holder], ObjectRef = shared };
        var unrelated = new TestNode(context);

        // Act & Assert
        Assert.True(StructuralMutator.CanReach(root, shared));
        Assert.True(StructuralMutator.CanReach(root, root));
        Assert.False(StructuralMutator.CanReach(shared, root));
        Assert.False(StructuralMutator.CanReach(root, unrelated));
    }

    [Fact]
    public void WhenCanReachWalksACycle_ThenItTerminates()
    {
        // Arrange: a <-> b.
        var context = CreateContext();
        var nodeB = new TestNode(context);
        var nodeA = new TestNode(context) { ObjectRef = nodeB };
        nodeB.ObjectRef = nodeA;
        var unrelated = new TestNode(context);

        // Act & Assert
        Assert.True(StructuralMutator.CanReach(nodeA, nodeB));
        Assert.False(StructuralMutator.CanReach(nodeA, unrelated));
    }

    [Fact]
    public void WhenDispatchWeightsAreInspected_ThenEveryKindIsScheduledAndSizingKeepsTheMajority()
    {
        // Arrange
        var weights = StructuralMutator.DispatchWeights;

        // Act
        var perKind = weights
            .GroupBy(kind => kind)
            .ToDictionary(group => group.Key, group => group.Count());

        // Assert: no kind is unreachable, and the sizing kinds still dominate so the graph keeps
        // growing and shrinking rather than only being rewired.
        foreach (var kind in Enum.GetValues<StructuralMutationKind>())
        {
            Assert.True(perKind.GetValueOrDefault(kind) > 0, $"{kind} is never scheduled.");
        }

        var sizing = perKind[StructuralMutationKind.Collection]
            + perKind[StructuralMutationKind.Dictionary]
            + perKind[StructuralMutationKind.ObjectRef];

        Assert.True(sizing * 2 > weights.Length, "Sizing kinds must keep the majority of the budget.");
        Assert.True(sizing < weights.Length, "Rewiring kinds must get a share of the budget.");
    }

    [Fact]
    public void WhenDrivenWithTheWeightedTable_ThenSharedReferencesAppear()
    {
        // Arrange: a graph wide enough that the rewiring kinds find an eligible shape.
        var context = CreateContext();
        var root = TestNode.CreateWithGraph(context, collectionCount: 12, dictionaryCount: 6);
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var mutator = new StructuralMutator(graph);

        var sawSharedReference = false;

        // Act: drive enough ticks that a 1-in-20 kind is all but certain to land.
        for (var i = 0; i < 3000 && !sawSharedReference; i++)
        {
            mutator.PerformMutation();
            graph.Rebuild(root);

            // A node with two parents shows up as an object reference that some other node also
            // holds in one of its containers.
            sawSharedReference = graph.KnownNodes.Any(node => node.ObjectRef is { } reference
                && graph.KnownNodes.Any(other => !ReferenceEquals(other, node) && HoldsChild(other, reference)));
        }

        // Assert
        Assert.True(sawSharedReference, "The weighted table must produce shared references.");
    }

    [Fact]
    public void WhenDrivenForManyTicks_ThenGraphBookkeepingStaysConsistentAndTheOracleStillCaptures()
    {
        // Arrange
        var context = CreateContext();
        var root = TestNode.CreateWithGraph(context, collectionCount: 12, dictionaryCount: 6);
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var mutator = new StructuralMutator(graph);

        // Act
        for (var i = 0; i < 1500; i++)
        {
            mutator.PerformMutation();

            if (i % 10 == 0)
            {
                graph.Rebuild(root);
            }
        }

        // Force one shared reference to survive to the end so the capture below is taken over a
        // real DAG rather than whatever tree the last ticks happened to leave behind.
        graph.Rebuild(root);
        Assert.True(mutator.PerformMutation(StructuralMutationKind.SharedReference, root));
        graph.Rebuild(root);
        Assert.Contains(graph.KnownNodes, node => !ReferenceEquals(node, root)
            && HoldsChild(node, root.ObjectRef!));

        // Assert: no duplicate bookkeeping entries, node count still bounded, and the convergence
        // oracle still terminates and produces one entry per distinct reachable node.
        Assert.Equal(graph.KnownNodes.Count, graph.KnownNodes.Distinct().Count());
        Assert.Equal(graph.StructuralTargets.Count, graph.StructuralTargets.Distinct().Count());

        // The ceiling is checked against the node count from the last rebuild, so up to one added
        // node per tick in the stale window can slip past it. The run loop rebuilds every 10 ticks.
        Assert.True(graph.KnownNodes.Count <= 510, $"Node count {graph.KnownNodes.Count} exceeds the ceiling plus the stale window.");
        Assert.Equal(ReachableNodes(root).Count, graph.KnownNodes.Count);
        AssertNoContainerHoldsANodeTwice(root);

        var snapshot = SnapshotComparer.Capture(root);
        Assert.Equal(graph.KnownNodes.Count, SnapshotComparer.CountSubjectsAndProperties(snapshot).Subjects);
    }
}
