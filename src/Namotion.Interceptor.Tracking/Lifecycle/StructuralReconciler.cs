using System.Collections;
using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>Reconciles a structural property's committed immutable occurrence snapshots.</summary>
internal sealed class StructuralReconciler(LifecycleNotifier notifier, OwnershipGraph graph, AttachTraversal attach, ReleaseTraversal release)
{
    public void Reconcile(
        PropertyReference property,
        SubjectPropertyMetadata metadata,
        object? newValue,
        long sourceRevision = 0,
        Dictionary<IInterceptorSubject, OwnershipReservationToken>? reservations = null)
    {
        var oldSnapshot = graph.GetSnapshot(property);
        var newSnapshot = StructuralSnapshotBuilder.Build(metadata.Type, newValue, sourceRevision);

        if (!graph.IsOwned(property.Subject) || !ReferenceEquals(graph.GetSnapshot(property), oldSnapshot))
        {
            return;
        }

        graph.SetSnapshot(property, newSnapshot);
        ReconcileOccurrences(property, newValue, oldSnapshot.Occurrences, newSnapshot.Occurrences, reservations);
    }

    private void ReconcileOccurrences(
        PropertyReference property,
        object? newValue,
        ImmutableArray<StructuralOccurrence> oldOccurrences,
        ImmutableArray<StructuralOccurrence> newOccurrences,
        Dictionary<IInterceptorSubject, OwnershipReservationToken>? reservations)
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

            for (var index = oldOccurrences.Length - 1; index >= 0; index--)
            {
                var occurrence = oldOccurrences[index];
                var remaining = oldCounts[occurrence.Subject];
                if (remaining <= newCounts.GetValueOrDefault(occurrence.Subject))
                {
                    continue;
                }

                oldCounts[occurrence.Subject] = remaining - 1;
                release.RemoveEdge(
                    occurrence.Subject,
                    property,
                    occurrence.SubjectOrdinal,
                    occurrence.Index);
                if (!graph.IsOwned(parent))
                {
                    return;
                }
            }

            foreach (var occurrence in newOccurrences)
            {
                var retained = oldCounts.GetValueOrDefault(occurrence.Subject);
                if (retained > 0)
                {
                    oldCounts[occurrence.Subject] = retained - 1;
                    continue;
                }

                attach.AttachEdge(
                    occurrence.Subject,
                    property,
                    occurrence.SubjectOrdinal,
                    occurrence.Index,
                    reservations);
                if (!graph.IsOwned(parent))
                {
                    return;
                }
            }

            RefreshRetainedIndices(property, newValue, oldOccurrences, newOccurrences, newCounts);
        }
        finally
        {
            LifecycleScratch.Return(oldCounts);
            LifecycleScratch.Return(newCounts);
        }
    }

    private void RefreshRetainedIndices(
        PropertyReference property,
        object? newValue,
        ImmutableArray<StructuralOccurrence> oldOccurrences,
        ImmutableArray<StructuralOccurrence> newOccurrences,
        Dictionary<IInterceptorSubject, int> newCounts)
    {
        if (newValue is not IEnumerable || newValue is string || newOccurrences.IsEmpty)
        {
            return;
        }

        var hasRetained = false;
        foreach (var occurrence in oldOccurrences)
        {
            if (occurrence.SubjectOrdinal < newCounts.GetValueOrDefault(occurrence.Subject))
            {
                hasRetained = true;
                break;
            }
        }

        if (!hasRetained)
        {
            return;
        }

        var indicesBySubject = LifecycleScratch.RentIndexGroups();
        try
        {
            foreach (var occurrence in newOccurrences)
            {
                if (!indicesBySubject.TryGetValue(occurrence.Subject, out var indices))
                {
                    indices = LifecycleScratch.RentIndexList();
                    indicesBySubject.Add(occurrence.Subject, indices);
                }

                indices.Add(occurrence.Index);
            }

            foreach (var entry in indicesBySubject)
            {
                var ownership = graph.TryGetOwnership(entry.Key);
                if (ownership is not null)
                {
                    ownership.UpdateIncomingIndices(property, entry.Value);
                    ownership.RepublishParents();
                }
            }
        }
        finally
        {
            LifecycleScratch.Return(indicesBySubject);
        }

        notifier.RefreshCollectionProperty(property, newValue);
    }
}
