namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The single reachability query the ownership model needs: does an anchored root lie in a subject's
/// ancestor closure.
/// </summary>
/// <remarks>
/// It is a backward search over committed incoming edges rather than a forward mark from the roots.
/// A subject is reachable from a root exactly when some root lies in its ancestor closure, so the
/// cost is that closure instead of the whole context. A complete context-local scan was measured at
/// 135 times master on a single shared-parent removal in a 2000-subject context; a forward mark is
/// invalidated by every cross-parent removal, which is the common shape; and incrementally
/// maintained reachability pays on every edge mutation including the tree-shaped removals that the
/// zero-remaining-edges short circuit already answers for free.
///
/// Two questions share this one walk. Release asks whether a subject that just lost an edge is still
/// held, and passes no exclusion. Anchor adoption asks whether a new edge supports the subject
/// independently of its own provisional anchor, and passes the edge's parent as the start and the
/// subject as the exclusion: an excluded subject is still traversed through, because its own
/// ancestors are genuinely independent support, but its anchor does not count.
/// </remarks>
internal sealed class ReachabilityWalk(OwnershipGraph graph)
{
    public bool HasAnchoredAncestor(IInterceptorSubject start, IInterceptorSubject? excluded)
    {
        if (!ReferenceEquals(start, excluded) && graph.IsAnchored(start))
        {
            return true;
        }

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

                foreach (var edge in edges)
                {
                    var parent = edge.Property.Subject;
                    if (visited.Contains(parent))
                    {
                        continue;
                    }

                    // A recorded incoming edge only counts once the parent still commits the
                    // matching outgoing edge: a reconcile commits the new property value before it
                    // updates the incoming records, so the two legitimately disagree in that window.
                    // A rejected parent is deliberately not marked visited, because it can still be
                    // a valid parent of another subject later in this same walk.
                    if (!graph.CommitsEdgeTo(edge.Property, current))
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
}
