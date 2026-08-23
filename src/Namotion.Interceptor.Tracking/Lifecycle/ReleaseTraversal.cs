namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Removes committed incoming edges and releases whatever a removal orphans, including closed
/// cycles.
/// </summary>
/// <remarks>
/// Release order is observable, so it is a traversal rather than a sweep: a mark-and-sweep yields an
/// unordered set and iterating the owned-subject map is nondeterministic. The descent starts at the
/// removed edge, follows committed outgoing edges, visits only subjects that lost their last
/// support, and reaches each of them exactly once in first-visit order. That makes detach callbacks
/// arrive top-down, so a parent stops before the children it uses.
///
/// A released subject's record and baselines are dropped before its callbacks run, which is what
/// makes the descent safe to re-enter from a callback: the re-entry finds the subject already gone,
/// and a reachability walk can no longer route through it.
/// </remarks>
internal sealed class ReleaseTraversal(LifecycleInterceptor lifecycle, OwnershipGraph graph, ReachabilityWalk reachability)
{
    /// <summary>
    /// Removes one committed incoming edge occurrence and releases the subject, and everything below
    /// it that this removal orphans, when nothing holds it anymore.
    /// </summary>
    public void RemoveEdge(IInterceptorSubject subject, PropertyReference property, object? index)
    {
        var ownership = graph.TryGetOwnership(subject);
        if (ownership is null || !ownership.RemoveIncoming(property, index))
        {
            // Already released, or the edge was drained by a reentrant descent.
            return;
        }

        var referenceCount = ownership.IncomingCount;
        ParentProjection.Publish(ownership);

        if (IsStillHeld(subject, ownership))
        {
            lifecycle.PublishEdgeRemoved(subject, property, index, referenceCount);
            return;
        }

        Release(subject, ownership, property, index);
    }

    /// <summary>
    /// Releases a subject that lost its root anchor, together with everything below it that only it
    /// held. Used by explicit detach and by the transitional forced detach.
    /// </summary>
    public void ReleaseRoot(IInterceptorSubject subject)
    {
        var ownership = graph.TryGetOwnership(subject);
        if (ownership is null)
        {
            return;
        }

        Release(subject, ownership, null, null);
    }

    /// <summary>
    /// Publishes a removal for every incoming edge the released subject still carries. Those edges
    /// come from inside the same unreachable component, typically the other half of a cycle. Without
    /// this they would never receive their removal notification, and the subject would report a
    /// nonzero reference count on the very change that takes it out of the graph.
    /// </summary>
    private void DrainRemainingEdges(IInterceptorSubject subject, SubjectOwnership ownership)
    {
        if (ownership.IncomingCount == 0)
        {
            return;
        }

        var remaining = LifecycleScratch.RentEdgeList();
        try
        {
            ownership.CopyIncomingEdges(remaining);
            foreach (var edge in remaining)
            {
                ownership.RemoveIncoming(edge.Property, edge.Index);
                ParentProjection.Publish(ownership);
                lifecycle.PublishEdgeRemoved(subject, edge.Property, edge.Index, ownership.IncomingCount);
            }
        }
        finally
        {
            LifecycleScratch.Return(remaining);
        }
    }

    /// <summary>
    /// Whether anything still holds the subject: its own anchor, or a path from an anchored root.
    /// The zero-edge short circuit is what keeps tree-shaped removals free of any walk.
    /// </summary>
    private bool IsStillHeld(IInterceptorSubject subject, SubjectOwnership ownership)
    {
        if (graph.IsAnchored(subject))
        {
            return true;
        }

        return ownership.IncomingCount > 0 && reachability.HasAnchoredAncestor(subject, null);
    }

    private void Release(IInterceptorSubject subject, SubjectOwnership ownership, PropertyReference? property, object? index)
    {
        var children = LifecycleScratch.RentChildList();
        graph.CollectCommittedChildren(subject, children);

        // Drop the ownership record and the baselines first: from here on the subject is released as
        // far as every other query is concerned, which is what makes the callbacks below safe to
        // re-enter this descent from.
        graph.RemoveOwnership(subject);
        graph.RemoveBaselines(subject);

        foreach (var entry in subject.Properties)
        {
            subject.DetachSubjectProperty(new PropertyReference(subject, entry.Key));
        }

        DrainRemainingEdges(subject, ownership);

        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = 0,
            IsPropertyReferenceRemoved = property.HasValue,
            IsContextDetach = true
        };

        lifecycle.RaiseSubjectDetaching(change);
        lifecycle.InvokeRemovedLifecycleHandlers(subject, change);

        // Only after the subject's own teardown callbacks completed, so they still resolve the
        // context they are being torn down from. The composed fallback contexts are left alone:
        // while the executor is still a context, they are what keeps a released subject's own
        // writes intercepted, and the handler that composed one is the one that removes it.
        graph.ReleaseClaim(subject);

        try
        {
            foreach (var (childProperty, occurrence) in children)
            {
                RemoveEdge(occurrence.Subject, childProperty, occurrence.Index);

                // A child that outlives this subject was resolving its services through it, because
                // that is what the composed context chain does. Reattach it to the exact context, or
                // it stays owned while its own writes stop being intercepted. The subtree services
                // the released subject carried correctly stop applying to it.
                if (graph.IsOwned(occurrence.Subject))
                {
                    occurrence.Subject.Context.AddFallbackContext(graph.Context);
                }
            }
        }
        finally
        {
            LifecycleScratch.Return(children);
        }
    }
}
