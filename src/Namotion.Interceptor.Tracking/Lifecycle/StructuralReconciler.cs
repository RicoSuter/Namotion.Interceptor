using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

internal sealed class StructuralReconciler(LifecycleNotifier notifier, OwnershipGraph graph)
{
    public OwnershipGraph.PreparedTopologyChange Prepare(
        PropertyReference property,
        StructuralSnapshot snapshot,
        Dictionary<PropertyReference, StructuralSnapshot> seededSnapshots,
        Dictionary<IInterceptorSubject, ImmutableArray<string>> seededPropertyNames,
        Dictionary<IInterceptorSubject, OwnershipReservationToken> reservations) =>
        graph.PrepareReconcile(
            property, snapshot, seededSnapshots, seededPropertyNames, reservations, notifier);

    public void Publish(OwnershipGraph.PreparedTopologyChange change) => graph.Publish(change);
}
