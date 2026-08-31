using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

internal sealed class AttachTraversal(LifecycleNotifier notifier, OwnershipGraph graph) {
    public ImmutableArray<StructuralSnapshotBuilder.CaptureParticipant> Capture(
        IInterceptorSubject root, HashSet<IInterceptorSubject> visited, List<IInterceptorSubject> discovered,
        Dictionary<PropertyReference, StructuralSnapshot> snapshots, Dictionary<IInterceptorSubject, ImmutableArray<string>> propertyNames) =>
        StructuralSnapshotBuilder.CaptureComponent(
            root, graph.Context, graph.State, visited, discovered, snapshots, propertyNames);
    public OwnershipGraph.PreparedTopologyChange Prepare(
        IInterceptorSubject root, SubjectAttachmentAnchorKind anchor, Dictionary<PropertyReference, StructuralSnapshot> snapshots,
        Dictionary<IInterceptorSubject, ImmutableArray<string>> propertyNames,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations) =>
        graph.PrepareAttach(root, anchor, snapshots, propertyNames, reservations, notifier);
}
