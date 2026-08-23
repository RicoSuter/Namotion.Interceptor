namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Records committed incoming edges and publishes the attach transitions they cause, and seeds a
/// subject's own structural properties when it enters the graph.
/// </summary>
/// <remarks>
/// The mirror image of <see cref="ReleaseTraversal"/>. Attach order is the one master already
/// publishes: the recursive descent happens from inside the context handler list, so handlers
/// ordered before it observe a subject top-down and handlers after it, along with the
/// <c>SubjectAttached</c> event, observe it bottom-up.
/// </remarks>
internal sealed class AttachTraversal(LifecycleInterceptor lifecycle, OwnershipGraph graph, ReachabilityWalk reachability)
{
    /// <summary>Seeds the subject's structural properties unless an earlier descent already did.</summary>
    public void SeedChildrenIfNeeded(IInterceptorSubject subject)
    {
        if (!graph.AreBaselinesSeeded(subject))
        {
            SeedAndAttachChildren(subject);
        }
    }

    public void SeedAndAttachChildren(IInterceptorSubject subject)
    {
        var children = LifecycleScratch.RentChildList();
        try
        {
            graph.SeedBaselines(subject, children);
            foreach (var (property, occurrence) in children)
            {
                AttachEdge(occurrence.Subject, property, occurrence.Index);
            }
        }
        finally
        {
            LifecycleScratch.Return(children);
        }
    }

    /// <summary>
    /// Records one incoming edge occurrence and publishes it, entering the subject into the graph
    /// when this is its first edge.
    /// </summary>
    public void AttachEdge(IInterceptorSubject subject, PropertyReference property, object? index)
    {
        var ownership = graph.TryGetOwnership(subject);
        var isContextAttach = ownership is null;
        if (isContextAttach)
        {
            if (!graph.TryClaim(subject, SubjectAnchorKind.None))
            {
                throw new InvalidOperationException(
                    $"The subject '{subject.GetType().Name}' is owned by a different context and cannot join this graph.");
            }

            ownership = graph.AddOwnership(subject);
        }

        ownership!.AddIncoming(property, index);
        var referenceCount = ownership.IncomingCount;

        // Authoritative parent and anchor state before the first handler observes the change.
        ParentProjection.Publish(ownership);
        ConsumeProvisionalAnchor(subject, property);

        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = referenceCount,
            IsContextAttach = isContextAttach,
            IsPropertyReferenceAdded = true
        };

        // Snapshotted before the handlers run: a handler may add properties, and those are attached
        // by that call rather than a second time here.
        var properties = subject.Properties.Keys;
        lifecycle.InvokeAddedLifecycleHandlers(subject, change);

        if (!isContextAttach)
        {
            return;
        }

        lifecycle.RaiseSubjectAttached(change);
        foreach (var propertyName in properties)
        {
            subject.AttachSubjectProperty(new PropertyReference(subject, propertyName));
        }
    }

    /// <summary>Publishes a subject entering the graph without an edge, as an anchored root.</summary>
    public void AttachRoot(IInterceptorSubject subject)
    {
        graph.AddOwnership(subject);

        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            ReferenceCount = 0,
            IsContextAttach = true
        };

        var properties = subject.Properties.Keys;
        lifecycle.InvokeAddedLifecycleHandlers(subject, change);

        lifecycle.RaiseSubjectAttached(change);
        foreach (var propertyName in properties)
        {
            subject.AttachSubjectProperty(new PropertyReference(subject, propertyName));
        }
    }

    /// <summary>
    /// Clears a provisional anchor once an edge supports the subject independently of that anchor,
    /// meaning the edge's parent has an anchored ancestor other than the subject itself.
    /// </summary>
    /// <remarks>
    /// Clearing on the first edge of any kind is unsound: the everyday back reference
    /// <c>child.Parent = root</c> would consume the root's own anchor, and the next removal anywhere
    /// would release the whole graph. A self edge fails the same test for the same reason.
    /// </remarks>
    private void ConsumeProvisionalAnchor(IInterceptorSubject subject, PropertyReference property)
    {
        var executor = subject.Executor;
        if (executor.Anchor != SubjectAnchorKind.Provisional || !ReferenceEquals(executor.AttachedContext, graph.Context))
        {
            return;
        }

        if (reachability.HasAnchoredAncestor(property.Subject, subject))
        {
            graph.ClearProvisionalAnchor(subject);
        }
    }
}
