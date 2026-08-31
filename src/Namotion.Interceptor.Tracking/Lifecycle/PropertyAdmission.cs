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
        Dictionary<IInterceptorSubject, CapturedSubjectProperties> PropertyNames,
        ImmutableArray<StructuralSnapshotBuilder.CaptureParticipant> Participants);

    internal Capture CaptureBatch(SubjectPropertyRegistration registration)
    {
        var subject = registration.Subject;
        var graphState = graph.State;
        var rootParticipant = StructuralSnapshotBuilder.CaptureParticipantState(subject, graphState);
        var batch = registration.GetProperties();
        var snapshots = new Dictionary<PropertyReference, StructuralSnapshot>(PropertyReference.Comparer);
        var propertyNames = new Dictionary<IInterceptorSubject, CapturedSubjectProperties>(
            ReferenceEqualityComparer.Instance);
        var addedNames = ImmutableArray.CreateBuilder<string>(batch.Count);
        var structuralProperties = ImmutableArray.CreateBuilder<PropertyReference>();
        var participants = ImmutableArray.CreateBuilder<StructuralSnapshotBuilder.CaptureParticipant>();
        var visited = LifecycleScratch.RentSubjectSet();
        try
        {
            participants.Add(rootParticipant);
            visited.Add(subject);
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
                    snapshot, graph.Context, graphState, visited, snapshots, propertyNames));
            }

            registration.PreparePublication(rootParticipant.Executor);
            if (rootParticipant.TryRefreshAfterCapture(graph.State, out var currentRoot))
            {
                participants[0] = currentRoot;
            }

            var names = ImmutableArray.CreateBuilder<string>(registration.PreparedProperties.Count);
            var preparedMetadata = ImmutableArray.CreateBuilder<SubjectPropertyMetadata>(registration.PreparedProperties.Count);
            foreach (var property in registration.PreparedProperties)
            {
                names.Add(property.Key);
                preparedMetadata.Add(property.Value);
            }

            propertyNames[subject] = new CapturedSubjectProperties(
                names.MoveToImmutable(), preparedMetadata.MoveToImmutable(),
                subject as ILifecycleHandler, subject as IPropertyLifecycleHandler);
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
        var properties = capture.PropertyNames[capture.Registration.Subject];
        notifier.AttachSubjectProperties(
            capture.Registration.Subject,
            properties.PropertyHandler,
            capture.Participants[0].Executor,
            properties.Metadata.Where(metadata => capture.AddedPropertyNames.Contains(metadata.Name)),
            capture.Snapshots,
            graph.State);
        return graph.PrepareAdmission(
            capture.Registration.Subject,
            capture.StructuralProperties,
            capture.Snapshots,
            capture.PropertyNames,
            reservations,
            notifier);
    }

}
