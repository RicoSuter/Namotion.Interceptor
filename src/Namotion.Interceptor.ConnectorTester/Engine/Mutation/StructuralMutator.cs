using System.Collections.Immutable;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// Performs one structural mutation against a target picked from KnownNodeGraph.StructuralTargets.
/// Caller is responsible for the loop, counter increments, and rebuild scheduling.
/// </summary>
/// <remarks>
/// Besides growing and shrinking the graph (<see cref="StructuralMutationKind.Collection"/>,
/// <see cref="StructuralMutationKind.Dictionary"/>, <see cref="StructuralMutationKind.ObjectRef"/>)
/// the mutator rewires it: it moves an existing node between parents, detaches and re-attaches the
/// same instance, permutes a collection, and points a second parent at an already-parented node so
/// the model becomes a DAG. Those four shapes are what let a run observe a receiver that fabricates
/// a phantom subject instead of reusing the one it already holds.
///
/// Shared references are kept acyclic: before an edge is created the mutator checks that the new
/// child cannot already reach the new parent. The check is best effort, because a connector can
/// apply a remote update between the check and the write. That residual race is survivable: every
/// graph walker involved in verification carries a visited set (KnownNodeGraph.Rebuild,
/// SubjectUpdateFactory.ProcessSubjectComplete and SnapshotIdMap.Build), so an accidental cycle
/// terminates rather than hangs.
/// </remarks>
public sealed class StructuralMutator
{
    private const int MinCollectionSize = 10;
    private const int MaxCollectionSize = 30;
    private const int MaxTotalNodes = 500;

    /// <summary>
    /// How many random picks a rewiring kind makes before giving up and falling back to a sizing
    /// kind. Structural targets include leaf nodes that hold no children and so cannot serve as a
    /// move or shared-reference source, which is why a single pick is not enough.
    /// </summary>
    private const int MaxCandidateAttempts = 16;

    /// <summary>
    /// The kinds that regulate the node count. They are the only ones scheduled once the graph
    /// sits at <see cref="MaxTotalNodes"/>, and they are the fallback when a rewiring kind finds
    /// no eligible shape in the current graph.
    /// </summary>
    private static readonly StructuralMutationKind[] SizingKinds =
    [
        StructuralMutationKind.Collection,
        StructuralMutationKind.Dictionary,
        StructuralMutationKind.ObjectRef
    ];

    /// <summary>
    /// Weighted dispatch table, one entry per 1/20th of the structural budget:
    /// collection 6, dictionary 6, object reference 2, cross-parent move 2, re-add 2,
    /// reorder 1, shared reference 1. The sizing kinds keep 70% so the graph still churns
    /// through add and remove at roughly its previous rate, while every rewiring shape still
    /// occurs at least once per 20 structural mutations on average.
    /// </summary>
    public static readonly ImmutableArray<StructuralMutationKind> DispatchWeights =
    [
        StructuralMutationKind.Collection,
        StructuralMutationKind.Collection,
        StructuralMutationKind.Collection,
        StructuralMutationKind.Collection,
        StructuralMutationKind.Collection,
        StructuralMutationKind.Collection,
        StructuralMutationKind.Dictionary,
        StructuralMutationKind.Dictionary,
        StructuralMutationKind.Dictionary,
        StructuralMutationKind.Dictionary,
        StructuralMutationKind.Dictionary,
        StructuralMutationKind.Dictionary,
        StructuralMutationKind.ObjectRef,
        StructuralMutationKind.ObjectRef,
        StructuralMutationKind.CrossParentMove,
        StructuralMutationKind.CrossParentMove,
        StructuralMutationKind.ReAdd,
        StructuralMutationKind.ReAdd,
        StructuralMutationKind.Reorder,
        StructuralMutationKind.SharedReference
    ];

    private readonly KnownNodeGraph _graph;
    private readonly IInterceptorSubjectContext? _transactionContext;
    private readonly Random _random = new();

    /// <param name="graph">The traversal state to pick mutation targets from.</param>
    /// <param name="context">
    /// Optional context used to commit a cross-parent move as a single update. Ignored when the
    /// context has no transaction support, in which case the move falls back to two plain writes.
    /// </param>
    public StructuralMutator(KnownNodeGraph graph, IInterceptorSubjectContext? context = null)
    {
        _graph = graph;
        _transactionContext = context?.TryGetService<SubjectTransactionInterceptor>() is not null
            ? context
            : null;
    }

    /// <summary>
    /// Performs one structural mutation without ever opening a transaction, so a cross-parent
    /// move reaches connectors as two updates. Use
    /// <see cref="PerformMutationAsync(CancellationToken)"/> to get the single-update variant.
    /// </summary>
    public void PerformMutation()
    {
        if (!TryPlanMutation(out var plan))
        {
            return;
        }

        ApplyInChangeScope(plan);
    }

    /// <summary>
    /// Performs one structural mutation of the requested kind against the given target, without
    /// opening a transaction. Returns false when the graph currently offers no eligible shape for
    /// that kind. The run loop uses the parameterless overloads, which pick the kind from the
    /// weighted table; this one exists so a caller can drive a single shape deterministically.
    /// </summary>
    public bool PerformMutation(StructuralMutationKind kind, TestNode target)
    {
        SnapshotGraph(out var targets, out var totalNodeCount);
        if (!TryPlanKind(kind, targets, target, totalNodeCount, out var plan))
        {
            return false;
        }

        ApplyInChangeScope(plan);
        return true;
    }

    /// <summary>
    /// Transactional counterpart of <see cref="PerformMutation(StructuralMutationKind, TestNode)"/>:
    /// a cross-parent move commits as a single update when the context supports transactions.
    /// </summary>
    public async ValueTask<bool> PerformMutationAsync(
        StructuralMutationKind kind, TestNode target, CancellationToken cancellationToken = default)
    {
        SnapshotGraph(out var targets, out var totalNodeCount);
        if (!TryPlanKind(kind, targets, target, totalNodeCount, out var plan))
        {
            return false;
        }

        if (_transactionContext is null || !RequiresSingleUpdate(plan.Kind))
        {
            ApplyInChangeScope(plan);
            return true;
        }

        using var transaction = await _transactionContext.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            cancellationToken: cancellationToken);

        ApplyInChangeScope(plan);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Performs one structural mutation, committing a cross-parent move in a transaction so the
    /// detach and the attach reach connectors as a single structural update.
    /// </summary>
    public async ValueTask PerformMutationAsync(CancellationToken cancellationToken = default)
    {
        if (!TryPlanMutation(out var plan))
        {
            return;
        }

        if (_transactionContext is null || !RequiresSingleUpdate(plan.Kind))
        {
            ApplyInChangeScope(plan);
            return;
        }

        using var transaction = await _transactionContext.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            cancellationToken: cancellationToken);

        ApplyInChangeScope(plan);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// A cross-parent move writes two properties. Committing them together is what produces the
    /// batch that contains a detach and an attach of the same subject.
    /// </summary>
    private static bool RequiresSingleUpdate(StructuralMutationKind kind)
        => kind == StructuralMutationKind.CrossParentMove;

    private void ApplyInChangeScope(in MutationPlan plan)
    {
        // No await inside this scope: SubjectChangeContext is thread static.
        using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
        {
            Apply(plan);
        }
    }

    private void Apply(in MutationPlan plan)
    {
        switch (plan.Kind)
        {
            case StructuralMutationKind.Collection:
                MutateCollection(plan.Target, plan.TotalNodeCount);
                break;
            case StructuralMutationKind.Dictionary:
                MutateDictionary(plan.Target, plan.TotalNodeCount);
                break;
            case StructuralMutationKind.ObjectRef:
                MutateObjectRef(plan.Target, plan.TotalNodeCount);
                break;
            case StructuralMutationKind.CrossParentMove:
                ApplyCrossParentMove(plan.Target, plan.Edge);
                break;
            case StructuralMutationKind.ReAdd:
                ApplyReAdd(plan.Edge);
                break;
            case StructuralMutationKind.Reorder:
                ApplyReorder(plan.Target);
                break;
            case StructuralMutationKind.SharedReference:
                ApplySharedReference(plan.Target, plan.Edge.Child);
                break;
        }
    }

    private void SnapshotGraph(out List<TestNode> targets, out int totalNodeCount)
    {
        lock (_graph.NodeLock)
        {
            // Rebuild publishes a fresh list rather than mutating the current one, so the
            // reference stays stable to read outside the lock.
            targets = _graph.StructuralTargets;
            totalNodeCount = _graph.KnownNodes.Count;
        }
    }

    private bool TryPlanMutation(out MutationPlan plan)
    {
        plan = default;

        SnapshotGraph(out var targets, out var totalNodeCount);
        if (targets.Count == 0)
        {
            return false;
        }

        var target = targets[_random.Next(targets.Count)];

        // At the node ceiling only the sizing kinds are scheduled: they are the ones that shrink
        // the graph back under the limit, and the rewiring kinds leave the node count unchanged.
        var kind = totalNodeCount >= MaxTotalNodes
            ? PickSizingKind()
            : DispatchWeights[_random.Next(DispatchWeights.Length)];

        if (TryPlanKind(kind, targets, target, totalNodeCount, out plan))
        {
            return true;
        }

        // The rewiring kinds need a specific shape (a populated parent, a second parent, a
        // collection with at least two entries). When the graph does not currently offer it,
        // fall back to a sizing kind so the tick still does useful work.
        plan = new MutationPlan(PickSizingKind(), target, totalNodeCount, default);
        return true;
    }

    private bool TryPlanKind(
        StructuralMutationKind kind, List<TestNode> targets, TestNode target, int totalNodeCount, out MutationPlan plan)
    {
        switch (kind)
        {
            case StructuralMutationKind.CrossParentMove:
                return TryPlanCrossParentMove(targets, target, totalNodeCount, out plan);

            case StructuralMutationKind.ReAdd:
                return TryPlanReAdd(target, totalNodeCount, out plan);

            case StructuralMutationKind.Reorder:
                return TryPlanReorder(target, totalNodeCount, out plan);

            case StructuralMutationKind.SharedReference:
                return TryPlanSharedReference(targets, target, totalNodeCount, out plan);

            default:
                plan = new MutationPlan(kind, target, totalNodeCount, default);
                return true;
        }
    }

    private StructuralMutationKind PickSizingKind() => SizingKinds[_random.Next(SizingKinds.Length)];

    private bool TryPlanCrossParentMove(
        List<TestNode> targets, TestNode destination, int totalNodeCount, out MutationPlan plan)
    {
        plan = default;

        for (var attempt = 0; attempt < MaxCandidateAttempts && targets.Count > 1; attempt++)
        {
            var source = targets[_random.Next(targets.Count)];
            if (ReferenceEquals(source, destination) || !TryPickChildOf(source, out var edge))
            {
                continue;
            }

            if (ReferenceEquals(edge.Child, destination) ||
                !CanAcceptChild(destination, edge.Child) ||
                CanReach(edge.Child, destination))
            {
                continue;
            }

            plan = new MutationPlan(StructuralMutationKind.CrossParentMove, destination, totalNodeCount, edge);
            return true;
        }

        return false;
    }

    private bool TryPlanReAdd(TestNode target, int totalNodeCount, out MutationPlan plan)
    {
        plan = default;

        if (!TryPickChildOf(target, out var edge))
        {
            return false;
        }

        plan = new MutationPlan(StructuralMutationKind.ReAdd, target, totalNodeCount, edge);
        return true;
    }

    private static bool TryPlanReorder(TestNode target, int totalNodeCount, out MutationPlan plan)
    {
        plan = default;

        var collection = target.Collection;
        if (collection is null || collection.Length < 2)
        {
            return false;
        }

        plan = new MutationPlan(StructuralMutationKind.Reorder, target, totalNodeCount, default);
        return true;
    }

    private bool TryPlanSharedReference(
        List<TestNode> targets, TestNode target, int totalNodeCount, out MutationPlan plan)
    {
        plan = default;

        for (var attempt = 0; attempt < MaxCandidateAttempts && targets.Count > 1; attempt++)
        {
            var source = targets[_random.Next(targets.Count)];
            if (ReferenceEquals(source, target) || !TryPickChildOf(source, out var edge))
            {
                continue;
            }

            // Skip a child that already is the target's reference: the equality interceptor would
            // suppress the write and the mutation would silently do nothing.
            if (ReferenceEquals(edge.Child, target) ||
                ReferenceEquals(target.ObjectRef, edge.Child) ||
                CanReach(edge.Child, target))
            {
                continue;
            }

            plan = new MutationPlan(StructuralMutationKind.SharedReference, target, totalNodeCount, edge);
            return true;
        }

        return false;
    }

    private void ApplyCrossParentMove(TestNode destination, in ChildEdge edge)
    {
        // Attach before detach so the node is never momentarily unreachable. In a transaction
        // both writes commit together; without one the receiver briefly sees a shared reference,
        // which is itself a shape worth exercising.
        if (!TryAttachChild(destination, edge.Child))
        {
            return;
        }

        DetachChild(edge);
    }

    private void ApplyReAdd(in ChildEdge edge)
    {
        // Two deliberately separate writes: the receiver must cope with an instance it has just
        // been told to detach reappearing under the same parent.
        DetachChild(edge);
        TryAttachChild(edge.Parent, edge.Child);
    }

    private void ApplyReorder(TestNode target)
    {
        var collection = target.Collection;
        if (collection is null || collection.Length < 2)
        {
            return;
        }

        var reordered = (TestNode[])collection.Clone();

        if (_random.Next(2) == 0)
        {
            var first = _random.Next(reordered.Length);
            var second = _random.Next(reordered.Length - 1);
            if (second >= first)
            {
                second++;
            }

            (reordered[first], reordered[second]) = (reordered[second], reordered[first]);
        }
        else
        {
            // Rotate left by one so every index changes, which yields a long run of move
            // operations rather than the two a swap produces.
            var head = reordered[0];
            Array.Copy(reordered, 1, reordered, 0, reordered.Length - 1);
            reordered[^1] = head;
        }

        target.Collection = reordered;
    }

    private static void ApplySharedReference(TestNode target, TestNode? child)
    {
        if (child is null || ReferenceEquals(target, child))
        {
            return;
        }

        target.ObjectRef = child;
    }

    /// <summary>
    /// Picks one of the node's collection or dictionary children, uniformly across both.
    /// Object references are never picked as a source edge, so a move or a shared reference always
    /// starts from a container entry.
    /// </summary>
    private bool TryPickChildOf(TestNode parent, out ChildEdge edge)
    {
        edge = default;

        var collection = parent.Collection;
        var items = parent.Items;
        var collectionCount = collection?.Length ?? 0;
        var itemCount = items?.Count ?? 0;

        if (collectionCount + itemCount == 0)
        {
            return false;
        }

        var pick = _random.Next(collectionCount + itemCount);
        if (pick < collectionCount)
        {
            edge = new ChildEdge(parent, collection![pick], DictionaryKey: null);
            return true;
        }

        var keys = items!.Keys.ToList();
        var key = keys[pick - collectionCount];
        edge = new ChildEdge(parent, items[key], key);
        return true;
    }

    /// <summary>
    /// Removes the edge's child from the container it was found in. Re-resolves the child by
    /// reference because the container may have been rewritten since the plan was made.
    /// </summary>
    private static void DetachChild(in ChildEdge edge)
    {
        if (edge.DictionaryKey is { } key)
        {
            var items = edge.Parent.Items;
            if (items is null || !items.TryGetValue(key, out var current) || !ReferenceEquals(current, edge.Child))
            {
                return;
            }

            var newItems = new Dictionary<string, TestNode>(items);
            newItems.Remove(key);
            edge.Parent.Items = newItems;
            return;
        }

        var collection = edge.Parent.Collection;
        if (collection is null)
        {
            return;
        }

        var index = IndexOf(collection, edge.Child);
        if (index < 0)
        {
            return;
        }

        edge.Parent.Collection = [.. collection[..index], .. collection[(index + 1)..]];
    }

    /// <summary>
    /// Appends the child to the parent's collection or adds it under a fresh dictionary key.
    /// Never produces a second entry for a node the container already holds, because one subject
    /// appearing twice inside one property is not a shape the model contract supports.
    /// </summary>
    private bool TryAttachChild(TestNode parent, TestNode child)
    {
        var collection = parent.Collection;
        var items = parent.Items;

        var canUseCollection = collection is not null
            && collection.Length < MaxCollectionSize
            && IndexOf(collection, child) < 0;

        var canUseDictionary = items is not null
            && items.Count < MaxCollectionSize
            && !ContainsValue(items, child);

        if (canUseCollection && (!canUseDictionary || _random.Next(2) == 0))
        {
            parent.Collection = [.. collection!, child];
            return true;
        }

        if (canUseDictionary)
        {
            parent.Items = new Dictionary<string, TestNode>(items!)
            {
                [$"item-{GlobalMutationCounter.Next()}"] = child
            };
            return true;
        }

        return false;
    }

    private static bool CanAcceptChild(TestNode parent, TestNode child)
    {
        var collection = parent.Collection;
        if (collection is not null && collection.Length < MaxCollectionSize && IndexOf(collection, child) < 0)
        {
            return true;
        }

        var items = parent.Items;
        return items is not null && items.Count < MaxCollectionSize && !ContainsValue(items, child);
    }

    /// <summary>
    /// Returns whether <paramref name="target"/> is reachable from <paramref name="from"/>.
    /// Used to keep new shared edges acyclic: an edge parent -> child is safe only when the child
    /// cannot already reach the parent.
    /// </summary>
    public static bool CanReach(TestNode from, TestNode target)
    {
        if (ReferenceEquals(from, target))
        {
            return true;
        }

        var visited = new HashSet<TestNode>();
        var pending = new Stack<TestNode>();
        pending.Push(from);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!visited.Add(node))
            {
                continue;
            }

            if (ReferenceEquals(node, target))
            {
                return true;
            }

            var collection = node.Collection;
            if (collection is not null)
            {
                foreach (var child in collection)
                {
                    pending.Push(child);
                }
            }

            var items = node.Items;
            if (items is not null)
            {
                foreach (var child in items.Values)
                {
                    pending.Push(child);
                }
            }

            var objectRef = node.ObjectRef;
            if (objectRef is not null)
            {
                pending.Push(objectRef);
            }
        }

        return false;
    }

    private static int IndexOf(TestNode[] collection, TestNode node)
    {
        for (var index = 0; index < collection.Length; index++)
        {
            if (ReferenceEquals(collection[index], node))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool ContainsValue(Dictionary<string, TestNode> items, TestNode node)
    {
        foreach (var value in items.Values)
        {
            if (ReferenceEquals(value, node))
            {
                return true;
            }
        }

        return false;
    }

    private void MutateCollection(TestNode target, int totalNodeCount)
    {
        var collection = target.Collection;
        var count = collection.Length;
        var atNodeLimit = totalNodeCount >= MaxTotalNodes;

        if (count >= MaxCollectionSize || (atNodeLimit && count > MinCollectionSize))
        {
            RemoveFromCollection(target, collection);
        }
        else if (count <= MinCollectionSize && !atNodeLimit)
        {
            AddToCollection(target, collection);
        }
        else if (atNodeLimit)
        {
            RemoveFromCollection(target, collection);
        }
        else
        {
            if (_random.Next(2) == 0)
            {
                AddToCollection(target, collection);
            }
            else
            {
                RemoveFromCollection(target, collection);
            }
        }
    }

    private void AddToCollection(TestNode target, TestNode[] collection)
    {
        target.Collection = [.. collection, new TestNode()];
    }

    private void RemoveFromCollection(TestNode target, TestNode[] collection)
    {
        if (collection.Length == 0)
        {
            return;
        }

        var index = _random.Next(collection.Length);
        target.Collection = [.. collection[..index], .. collection[(index + 1)..]];
    }

    private void MutateDictionary(TestNode target, int totalNodeCount)
    {
        var items = target.Items;
        var count = items.Count;
        var atNodeLimit = totalNodeCount >= MaxTotalNodes;

        if (count >= MaxCollectionSize || (atNodeLimit && count > MinCollectionSize))
        {
            RemoveFromDictionary(target, items);
        }
        else if (count <= MinCollectionSize && !atNodeLimit)
        {
            AddToDictionary(target);
        }
        else if (atNodeLimit)
        {
            RemoveFromDictionary(target, items);
        }
        else
        {
            if (_random.Next(2) == 0)
            {
                AddToDictionary(target);
            }
            else
            {
                RemoveFromDictionary(target, items);
            }
        }
    }

    private void AddToDictionary(TestNode target)
    {
        var uniqueKey = $"item-{GlobalMutationCounter.Next()}";
        var newItems = new Dictionary<string, TestNode>(target.Items)
        {
            [uniqueKey] = new TestNode()
        };
        target.Items = newItems;
    }

    private void RemoveFromDictionary(TestNode target, Dictionary<string, TestNode> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var keys = items.Keys.ToList();
        var key = keys[_random.Next(keys.Count)];
        var newItems = new Dictionary<string, TestNode>(items);
        newItems.Remove(key);
        target.Items = newItems;
    }

    private void MutateObjectRef(TestNode target, int totalNodeCount)
    {
        var atNodeLimit = totalNodeCount >= MaxTotalNodes;

        if (target.ObjectRef != null && (_random.Next(2) == 0 || atNodeLimit))
        {
            target.ObjectRef = null;
        }
        else if (!atNodeLimit)
        {
            target.ObjectRef = new TestNode();
        }
    }

    /// <summary>An existing parent -> child edge, located either in a collection or under a dictionary key.</summary>
    private readonly record struct ChildEdge(TestNode Parent, TestNode Child, string? DictionaryKey);

    /// <summary>A resolved mutation: what to do, to which node, with which existing edge.</summary>
    private readonly record struct MutationPlan(
        StructuralMutationKind Kind,
        TestNode Target,
        int TotalNodeCount,
        ChildEdge Edge);
}
