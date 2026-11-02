using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The single reachability query the ownership model needs: does an anchored root lie in a subject's
/// ancestor closure.
/// </summary>
/// <remarks>
/// It is a backward search over committed incoming edges rather than a forward mark from the roots.
/// A subject is reachable from a root exactly when some root lies in its ancestor closure, so the
/// cost is that closure instead of the whole context.
///
/// Two questions share this one walk. Release asks whether a subject that just lost an edge is still
/// held, and passes no exclusion. Anchor adoption asks whether a new edge supports the subject
/// independently of its own provisional anchor, and passes the edge's parent as the start and the
/// subject as the exclusion: an excluded subject is still traversed through, because its own
/// ancestors are genuinely independent support, but its anchor does not count.
/// </remarks>
internal sealed class ReachabilityWalk(OwnershipGraph graph)
{
    // Only bounds a cycle of one-edge subjects, which the reduction below cannot terminate on its
    // own. The search resumes where the reduction stopped rather than restarting, so a chain deeper
    // than this pays nothing for the reduction beyond the steps it took.
    private const int MaximumReductionSteps = 8;

    /// <summary>
    /// Whether an anchor holds <paramref name="start"/>: either its own, or one in its ancestor
    /// closure over committed incoming edges. <paramref name="excluded"/>, when given, does not
    /// count as an anchor anywhere in the walk, though the walk still passes through it.
    /// </summary>
    public bool IsAnchorReachable(IInterceptorSubject start, IInterceptorSubject? excluded)
    {
        if (!ReferenceEquals(start, excluded) && graph.IsAnchored(start))
        {
            return true;
        }

        // A subject whose only support is one committed edge asks the identical question of that
        // edge's parent, so while the frontier cannot branch the answer needs no visited set, no
        // stack and no edge copy. Every shape the search exists for leaves the reduction on the
        // first subject that has anything other than exactly one edge, and a closed cycle of
        // one-edge subjects closes back onto the start.
        var current = start;
        for (var step = 0; step < MaximumReductionSteps; step++)
        {
            switch (ReduceToParent(ref current, start, excluded))
            {
                case Reduction.Reachable:
                    return true;
                case Reduction.Unreachable:
                    return false;
                case Reduction.Branches:
                    return SearchAncestors(current, excluded);
            }
        }

        // Correct to resume from here rather than from the start: the reduction proved the closure
        // of every subject it left behind to be the closure of this one, and anchored none of them.
        return SearchAncestors(current, excluded);
    }

    private enum Reduction
    {
        Continued,
        Reachable,
        Unreachable,
        Branches
    }

    /// <summary>
    /// Moves <paramref name="current"/> to the parent of its only committed edge, or reports why the
    /// reduction ends at it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Reduction ReduceToParent(ref IInterceptorSubject current, IInterceptorSubject start, IInterceptorSubject? excluded)
    {
        var ownership = graph.TryGetOwnership(current);
        if (ownership is null || ownership.IncomingCount == 0)
        {
            // Nothing above it, and so nothing above the subjects reduced to reach it either.
            return Reduction.Unreachable;
        }

        if (!ownership.TryGetSingleIncoming(out var incoming))
        {
            return Reduction.Branches;
        }

        // The same validation the search applies to every candidate parent; its sole edge being
        // uncommitted leaves the closure empty rather than merely dropping one candidate.
        if (!graph.CommitsEdgeTo(incoming, current))
        {
            return Reduction.Unreachable;
        }

        var parent = incoming.Subject;
        if (ReferenceEquals(parent, start))
        {
            // Back where it began, so the closure is exactly the chain just walked and every
            // subject on it was tested. The step bound is what ends a cycle the start is not on.
            return Reduction.Unreachable;
        }

        if (!ReferenceEquals(parent, excluded) && graph.IsAnchored(parent))
        {
            return Reduction.Reachable;
        }

        current = parent;
        return Reduction.Continued;
    }

    private bool SearchAncestors(IInterceptorSubject start, IInterceptorSubject? excluded)
    {
        var visited = LifecycleScratch.RentSubjectSet();
        var expandable = LifecycleScratch.RentSubjectStack();
        var edges = LifecycleScratch.RentEdgeList();
        try
        {
            visited.Add(start);
            expandable.Push(start);

            while (expandable.Count > 0)
            {
                var current = expandable.Pop();
                var ownership = graph.TryGetOwnership(current);
                if (ownership is null)
                {
                    continue;
                }

                edges.Clear();
                ownership.CopyIncomingEdges(edges);
                if (ExpandParents(current, edges, visited, expandable, excluded))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            LifecycleScratch.Return(visited);
            LifecycleScratch.Return(expandable);
            LifecycleScratch.Return(edges);
        }
    }

    /// <summary>
    /// Pushes the unvisited parents that still commit their edge to <paramref name="current"/>, and
    /// reports whether one of them is anchored.
    /// </summary>
    private bool ExpandParents(
        IInterceptorSubject current,
        List<IncomingEdge> edges,
        HashSet<IInterceptorSubject> visited,
        Stack<IInterceptorSubject> expandable,
        IInterceptorSubject? excluded)
    {
        foreach (var edge in edges)
        {
            var parent = edge.Property.Subject;

            // A recorded incoming edge only counts once the parent still commits the matching
            // outgoing edge: a reconcile commits the new property value before it updates the
            // incoming records, so the two legitimately disagree in that window. A rejected parent
            // is deliberately not marked visited, because it can still be a valid parent of another
            // subject later in this same walk.
            if (visited.Contains(parent) || !graph.CommitsEdgeTo(edge.Property, current))
            {
                continue;
            }

            visited.Add(parent);
            if (!ReferenceEquals(parent, excluded) && graph.IsAnchored(parent))
            {
                return true;
            }

            expandable.Push(parent);
        }

        return false;
    }
}
