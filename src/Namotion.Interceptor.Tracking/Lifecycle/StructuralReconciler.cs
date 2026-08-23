using System.Collections;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Turns a committed structural property value into edge additions and removals against the value
/// the lifecycle last reconciled.
/// </summary>
/// <remarks>
/// Every occurrence is one edge, so <c>[a, a, b]</c> gives <c>a</c> two edges. Retained occurrences
/// are matched deterministically: ordinal values match by occurrence count in enumeration order, so
/// the first <c>min(old, new)</c> occurrences of a subject survive and only the surplus is removed;
/// keyed values match by key, because a key is a stable identity while an ordinal shifts on every
/// insertion.
///
/// The new value is committed as the property baseline before any edge is published. Incoming
/// records and committed outgoing edges therefore disagree for the duration of the publication,
/// which is exactly why every reader validates candidate edges against the baselines.
/// </remarks>
internal sealed class StructuralReconciler(LifecycleNotifier notifier, OwnershipGraph graph, AttachTraversal attach, ReleaseTraversal release)
{
    public void Reconcile(PropertyReference property, SubjectPropertyMetadata metadata, object? newValue)
    {
        var oldValue = graph.GetBaseline(property);
        if (ReferenceEquals(oldValue, newValue))
        {
            return;
        }

        if (!StructuralValueScanner.CanHoldSubjects(oldValue) && !StructuralValueScanner.CanHoldSubjects(newValue))
        {
            return;
        }

        var oldOccurrences = LifecycleScratch.RentOccurrenceList();
        var newOccurrences = LifecycleScratch.RentOccurrenceList();
        try
        {
            StructuralValueScanner.CollectOccurrences(property, oldValue, oldOccurrences);
            StructuralValueScanner.CollectOccurrences(property, newValue, newOccurrences);

            // Commit the outgoing edges before the incoming records are touched.
            graph.SetBaseline(property, newValue);

            if (StructuralValueScanner.HasKeyedOccurrences(metadata, oldValue) ||
                StructuralValueScanner.HasKeyedOccurrences(metadata, newValue))
            {
                ReconcileKeyed(property, oldValue, newValue, oldOccurrences, newOccurrences);
            }
            else
            {
                ReconcileOrdinal(property, newValue, oldOccurrences, newOccurrences);
            }
        }
        finally
        {
            LifecycleScratch.Return(oldOccurrences);
            LifecycleScratch.Return(newOccurrences);
        }
    }

    /// <summary>
    /// Keyed reconciliation: an occurrence survives when the other value holds the same subject under
    /// the same key. Both lookups are O(1) on a dictionary, so a large map costs one pass.
    /// </summary>
    private void ReconcileKeyed(
        PropertyReference property,
        object? oldValue,
        object? newValue,
        List<SubjectOccurrence> oldOccurrences,
        List<SubjectOccurrence> newOccurrences)
    {
        var parent = property.Subject;
        var hasRetained = false;

        for (var i = oldOccurrences.Count - 1; i >= 0; i--)
        {
            var occurrence = oldOccurrences[i];
            if (IsHeldAt(newValue, occurrence))
            {
                hasRetained = true;
                continue;
            }

            release.RemoveEdge(occurrence.Subject, property, occurrence.Index);
            if (!graph.IsOwned(parent))
            {
                // A reentrant descent released the writing parent mid-publication, entered from
                // an exempt attach or detach property callback, or from a third-party write
                // interceptor running downstream of the lifecycle. The remaining edges belong to
                // a subject that is no longer in the graph, so publishing them would claim on
                // behalf of a released owner.
                return;
            }
        }

        foreach (var occurrence in newOccurrences)
        {
            if (IsHeldAt(oldValue, occurrence))
            {
                continue;
            }

            attach.AttachEdge(occurrence.Subject, property, occurrence.Index);
            if (!graph.IsOwned(parent))
            {
                return;
            }
        }

        if (!graph.IsOwned(parent))
        {
            return;
        }

        // Keys are stable identities, so no index is rewritten here, but property handlers still
        // resynchronize their own collection projections against the committed value.
        if (hasRetained)
        {
            notifier.RefreshCollectionProperty(property, newValue);
        }
    }

    private static bool IsHeldAt(object? value, SubjectOccurrence occurrence)
    {
        return value is not null && occurrence.Index is not null &&
               ReferenceEquals(SubjectLookup.FindSubjectInDictionary(value, occurrence.Index), occurrence.Subject);
    }

    /// <summary>
    /// Ordinal reconciliation: per subject, the surplus old occurrences are removed from the end and
    /// the surplus new occurrences are added at the front-most free positions, then every retained
    /// edge adopts its new index.
    /// </summary>
    private void ReconcileOrdinal(
        PropertyReference property,
        object? newValue,
        List<SubjectOccurrence> oldOccurrences,
        List<SubjectOccurrence> newOccurrences)
    {
        var parent = property.Subject;
        var oldCounts = LifecycleScratch.RentSubjectCounter();
        var newCounts = LifecycleScratch.RentSubjectCounter();
        try
        {
            foreach (var occurrence in oldOccurrences)
            {
                oldCounts[occurrence.Subject] = oldCounts.GetValueOrDefault(occurrence.Subject) + 1;
            }

            foreach (var occurrence in newOccurrences)
            {
                newCounts[occurrence.Subject] = newCounts.GetValueOrDefault(occurrence.Subject) + 1;
            }

            // Removals run in reverse so the surviving occurrences are the leading ones, which is what
            // makes retained duplicates match in enumeration order. Reverse order is also what the
            // existing collection-child bookkeeping expects.
            for (var i = oldOccurrences.Count - 1; i >= 0; i--)
            {
                var occurrence = oldOccurrences[i];
                var remaining = oldCounts[occurrence.Subject];
                if (remaining <= newCounts.GetValueOrDefault(occurrence.Subject))
                {
                    continue;
                }

                oldCounts[occurrence.Subject] = remaining - 1;
                release.RemoveEdge(occurrence.Subject, property, occurrence.Index);
                if (!graph.IsOwned(parent))
                {
                    // See ReconcileKeyed: a reentrant callback descent released the writing parent.
                    return;
                }
            }

            // After the removal pass oldCounts holds min(old, new) per subject, which is exactly how
            // many leading new occurrences are already covered by a retained edge.
            foreach (var occurrence in newOccurrences)
            {
                var retained = oldCounts.GetValueOrDefault(occurrence.Subject);
                if (retained > 0)
                {
                    oldCounts[occurrence.Subject] = retained - 1;
                    continue;
                }

                attach.AttachEdge(occurrence.Subject, property, occurrence.Index);
                if (!graph.IsOwned(parent))
                {
                    return;
                }
            }

            if (!graph.IsOwned(parent))
            {
                return;
            }

            RefreshRetainedIndices(property, newValue, oldOccurrences, newOccurrences, newCounts);
        }
        finally
        {
            LifecycleScratch.Return(oldCounts);
            LifecycleScratch.Return(newCounts);
        }
    }

    /// <summary>
    /// Rewrites the occurrence indices of every subject in the new value, then lets property handlers
    /// refresh their own collection projections. A retained edge keeps its identity across a reorder,
    /// so it changes index without an attach or detach transition.
    /// </summary>
    private void RefreshRetainedIndices(
        PropertyReference property,
        object? newValue,
        List<SubjectOccurrence> oldOccurrences,
        List<SubjectOccurrence> newOccurrences,
        Dictionary<IInterceptorSubject, int> newCounts)
    {
        if (newValue is not IEnumerable || newValue is string || newOccurrences.Count == 0)
        {
            return;
        }

        var hasRetained = false;
        foreach (var occurrence in oldOccurrences)
        {
            if (newCounts.ContainsKey(occurrence.Subject))
            {
                hasRetained = true;
                break;
            }
        }

        if (!hasRetained)
        {
            return;
        }

        // An append leaves every retained occurrence at the index it already carries, which is the
        // common bulk-assignment shape, so it skips the rewrite entirely.
        if (!IsAppendOnly(oldOccurrences, newOccurrences))
        {
            var groups = LifecycleScratch.RentIndexGroups();
            try
            {
                foreach (var occurrence in newOccurrences)
                {
                    if (!groups.TryGetValue(occurrence.Subject, out var indices))
                    {
                        indices = LifecycleScratch.RentIndexList();
                        groups.Add(occurrence.Subject, indices);
                    }

                    indices.Add(occurrence.Index);
                }

                foreach (var group in groups)
                {
                    var ownership = graph.TryGetOwnership(group.Key);
                    if (ownership is null)
                    {
                        continue;
                    }

                    ownership.SetIncomingIndices(property, group.Value);
                    ownership.RepublishParents();
                }
            }
            finally
            {
                LifecycleScratch.Return(groups);
            }
        }

        notifier.RefreshCollectionProperty(property, newValue);
    }

    private static bool IsAppendOnly(List<SubjectOccurrence> oldOccurrences, List<SubjectOccurrence> newOccurrences)
    {
        if (newOccurrences.Count < oldOccurrences.Count)
        {
            return false;
        }

        for (var i = 0; i < oldOccurrences.Count; i++)
        {
            if (!ReferenceEquals(oldOccurrences[i].Subject, newOccurrences[i].Subject) ||
                !Equals(oldOccurrences[i].Index, newOccurrences[i].Index))
            {
                return false;
            }
        }

        return true;
    }
}
