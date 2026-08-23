using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Admits an <see cref="IInterceptorSubject.AddProperties"/> batch on an owned subject: one atomic
/// publication of metadata, property callbacks and initial ownership edges, or nothing. Runs under
/// the lifecycle's topology gate, entered by
/// <see cref="LifecycleInterceptor.TryAddProperties"/>.
/// </summary>
/// <remarks>
/// The order is the load-bearing part. The batch is materialized and duplicate-validated first,
/// then every qualifying getter is invoked exactly once and its result captured, then the complete
/// prospective component is discovered and claimed, and only then does anything publish: the
/// metadata swap, the property callbacks in input order, and finally the captured values as
/// ordinary structural assignments. Everything before the metadata swap can fail, and failing
/// there publishes nothing and releases the provisional claims. Everything after it is
/// exception-free by contract, and a violating callback propagates without rollback, like every
/// other lifecycle callback.
/// </remarks>
internal sealed class PropertyAdmission(OwnershipGraph graph, StructuralReconciler reconciler)
{
    public void Admit(SubjectPropertyRegistrationContext registration)
    {
        var subject = registration.Subject;
        var batch = registration.GetProperties();
        if (batch.Count == 0)
        {
            return;
        }

        // Classify the initial ownership candidates and invoke each qualifying getter exactly
        // once, before anything publishes. The captured value is authoritative for the property's
        // initial stored value: it is committed below rather than re-read, so a getter that is not
        // stable would otherwise commit edges for a graph nobody stored.
        List<(SubjectPropertyMetadata Metadata, object? Value)>? captured = null;
        foreach (var metadata in batch)
        {
            if (OwnershipGraph.IsStructural(metadata))
            {
                (captured ??= []).Add((metadata, metadata.GetValue?.Invoke(subject)));
            }
        }

        if (captured is null)
        {
            registration.Publish();
            InvokePropertyAttachCallbacks(subject, batch);
            return;
        }

        var visited = LifecycleScratch.RentSubjectSet();
        var claimed = LifecycleScratch.RentSubjectList();
        try
        {
            foreach (var (metadata, value) in captured)
            {
                if (value is not null)
                {
                    graph.DiscoverComponent(metadata.Type, value, visited, claimed);
                }
            }

            if (!graph.TryClaimDiscovered(claimed, null, SubjectAnchorKind.None))
            {
                claimed.Clear();
                throw new InvalidOperationException(
                    "Another context claimed a subject of the admitted graph while this call was " +
                    "validating it. Nothing was published.");
            }

            registration.Publish();
            InvokePropertyAttachCallbacks(subject, batch);

            // Commit the captured values as ordinary assignments: the reconciler sees no baseline,
            // so every occurrence becomes a fresh edge, and the value becomes the baseline later
            // writes diff against. A null value writes the baseline entry directly, because the
            // seeded-baseline invariant is per property: every structural property of an owned
            // subject carries an entry, present or null, and AreBaselinesSeeded reads whichever
            // enumerates first.
            foreach (var (metadata, value) in captured)
            {
                var property = new PropertyReference(subject, metadata.Name);
                if (value is null)
                {
                    graph.SetBaseline(property, null);
                }
                else
                {
                    reconciler.Reconcile(property, metadata, value);
                }
            }
        }
        finally
        {
            // Claims that never became ownership are handed back; see
            // OwnershipGraph.ReleaseUnusedClaims for what leaves them behind.
            graph.ReleaseUnusedClaims(claimed);
            LifecycleScratch.Return(visited);
            LifecycleScratch.Return(claimed);
        }
    }

    private static void InvokePropertyAttachCallbacks(IInterceptorSubject subject, IReadOnlyList<SubjectPropertyMetadata> batch)
    {
        for (var index = 0; index < batch.Count; index++)
        {
            subject.AttachSubjectProperty(new PropertyReference(subject, batch[index].Name));
        }
    }
}
