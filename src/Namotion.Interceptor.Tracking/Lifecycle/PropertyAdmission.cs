using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>Captures and publishes one lifecycle-aware property batch.</summary>
internal sealed class PropertyAdmission(LifecycleNotifier notifier, OwnershipGraph graph)
{
    internal sealed record Capture(
        SubjectPropertyRegistration Registration,
        ImmutableArray<string> AddedPropertyNames,
        ImmutableArray<PropertyReference> StructuralProperties,
        Dictionary<PropertyReference, StructuralSnapshot> Snapshots,
        Dictionary<IInterceptorSubject, ImmutableArray<string>> PropertyNames,
        ImmutableArray<StructuralSnapshotBuilder.CaptureParticipant> Participants);

    internal Capture CaptureBatch(
        SubjectPropertyRegistration registration,
        List<IInterceptorSubject> discovered)
    {
        var subject = registration.Subject;
        var graphState = graph.State;
        var rootParticipant = StructuralSnapshotBuilder.CaptureParticipantState(subject, graphState);
        var batch = registration.GetProperties();
        var snapshots = new Dictionary<PropertyReference, StructuralSnapshot>(PropertyReference.Comparer);
        var propertyNames = new Dictionary<IInterceptorSubject, ImmutableArray<string>>(
            ReferenceEqualityComparer.Instance);
        var addedNames = ImmutableArray.CreateBuilder<string>(batch.Count);
        var structuralProperties = ImmutableArray.CreateBuilder<PropertyReference>();
        var participants = ImmutableArray.CreateBuilder<StructuralSnapshotBuilder.CaptureParticipant>();
        var visited = LifecycleScratch.RentSubjectSet();
        try
        {
            participants.Add(rootParticipant);
            visited.Add(subject);
            discovered.Add(subject);
            foreach (var metadata in batch)
            {
                addedNames.Add(metadata.Name);
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
                    snapshot, graph.Context, graphState, visited, discovered, snapshots, propertyNames));
            }

            registration.PreparePublication(rootParticipant.Executor);
            if (rootParticipant.TryRefreshAfterCapture(graph.State, out var currentRoot))
            {
                participants[0] = currentRoot;
            }

            propertyNames[subject] = registration.PreparedProperties.Keys.ToImmutableArray();
            return new Capture(
                registration,
                addedNames.MoveToImmutable(),
                structuralProperties.ToImmutable(),
                snapshots,
                propertyNames,
                participants.ToImmutable());
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
        notifier.AttachSubjectProperties(capture.Registration.Subject, capture.AddedPropertyNames);
        return graph.PrepareAdmission(
            capture.Registration.Subject,
            capture.StructuralProperties,
            capture.Snapshots,
            capture.PropertyNames,
            reservations,
            notifier);
    }

    internal bool Publish(Capture capture, OwnershipGraph.PreparedTopologyChange change)
    {
        if (!capture.Registration.PublishPrepared(capture.Participants[0].Executor)) return false;
        graph.Publish(change);
        return true;
    }
}
