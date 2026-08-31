using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>Captures and publishes one lifecycle-aware property batch.</summary>
internal sealed class PropertyAdmission(LifecycleNotifier notifier, OwnershipGraph graph)
{
    internal sealed record Capture(
        SubjectPropertyRegistration Registration,
        IReadOnlyList<SubjectPropertyMetadata> AddedProperties,
        ImmutableArray<PropertyReference> StructuralProperties,
        Dictionary<PropertyReference, StructuralSnapshot> Snapshots,
        Dictionary<IInterceptorSubject, ImmutableArray<SubjectPropertyMetadata>> SubjectProperties,
        ImmutableArray<StructuralSnapshotBuilder.CaptureParticipant> Participants,
        long ProjectionRevisionCapacity);

    internal Capture CaptureBatch(SubjectPropertyRegistration registration)
    {
        var subject = registration.Subject;
        var graphState = graph.State;
        var rootParticipant = StructuralSnapshotBuilder.CaptureParticipantState(subject, graphState);
        var batch = registration.GetProperties();
        var snapshots = new Dictionary<PropertyReference, StructuralSnapshot>(PropertyReference.Comparer);
        var subjectProperties = new Dictionary<IInterceptorSubject, ImmutableArray<SubjectPropertyMetadata>>(
            ReferenceEqualityComparer.Instance);
        var structuralProperties = ImmutableArray.CreateBuilder<PropertyReference>();
        var participants = ImmutableArray.CreateBuilder<StructuralSnapshotBuilder.CaptureParticipant>();
        var visited = LifecycleScratch.RentSubjectSet();
        try
        {
            participants.Add(rootParticipant);
            visited.Add(subject);
            foreach (var metadata in batch)
            {
                if (!OwnershipGraph.IsStructural(metadata))
                {
                    continue;
                }

                var property = new PropertyReference(subject, metadata.Name);
                var snapshot = StructuralSnapshotBuilder.Build(
                    metadata.Type, metadata.GetValue?.Invoke(subject), 0);
                structuralProperties.Add(property);
                snapshots.Add(property, snapshot);
                participants.AddRange(StructuralSnapshotBuilder.CaptureComponent(
                    snapshot, graph.Context, graphState, visited, snapshots, subjectProperties));
            }

            registration.PreparePublication(rootParticipant.Executor);
            if (rootParticipant.TryRefreshAfterCapture(graph.State, out var currentRoot))
            {
                participants[0] = currentRoot;
            }

            var preparedMetadata = ImmutableArray.CreateBuilder<SubjectPropertyMetadata>(registration.PreparedProperties.Count);
            foreach (var property in registration.PreparedProperties)
            {
                preparedMetadata.Add(property.Value);
            }

            subjectProperties[subject] = preparedMetadata.MoveToImmutable();
            var capturedParticipants = participants.ToImmutable();
            long projectionRevisionCapacity = batch.Count;
            checked
            {
                foreach (var snapshot in snapshots.Values)
                    projectionRevisionCapacity += snapshot.Occurrences.Length;
                foreach (var participant in capturedParticipants)
                    if (participant.Ownership is null)
                        projectionRevisionCapacity += subjectProperties[participant.Subject].Length;
            }

            return new Capture(
                registration,
                batch,
                structuralProperties.ToImmutable(),
                snapshots,
                subjectProperties,
                capturedParticipants,
                projectionRevisionCapacity);
        }
        finally
        {
            LifecycleScratch.Return(visited);
        }
    }

    internal OwnershipGraph.PreparedTopologyChange Prepare(
        Capture capture,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations)
    {
        var graphEntryStart = notifier.JournalEntryCount;
        var change = graph.PrepareAdmission(
            capture.Registration.Subject,
            capture.StructuralProperties,
            capture.Snapshots,
            capture.SubjectProperties,
            reservations,
            notifier);
        // The completed graph is needed for the property payload, while callbacks still publish
        // the newly registered parent property before any of its initial child edges.
        var graphEntries = notifier.DeferJournalEntriesFrom(graphEntryStart);
        try
        {
            var publication = change.Publication;
            notifier.AttachSubjectProperties(
                capture.Registration.Subject,
                capture.Registration.Subject as IPropertyLifecycleHandler,
                capture.AddedProperties,
                publication.Snapshots,
                publication);
            notifier.AppendJournalEntries(graphEntries);
            return change;
        }
        catch
        {
            notifier.AppendJournalEntries(graphEntries);
            change.Dispose();
            throw;
        }
    }

}
