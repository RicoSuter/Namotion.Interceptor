using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

internal sealed class StructuralReconciler(LifecycleNotifier notifier, OwnershipGraph graph)
{
    public void Reconcile(
        PropertyReference property,
        SubjectPropertyMetadata metadata,
        object? newValue,
        long sourceRevision = 0,
        Dictionary<IInterceptorSubject, OwnershipReservationToken>? reservations = null)
    {
        var refreshCollection = graph.Reconcile(
            property,
            StructuralSnapshotBuilder.Build(metadata.Type, newValue, sourceRevision),
            reservations,
            notifier);
        if (refreshCollection)
        {
            notifier.RefreshCollectionProperty(property, newValue);
        }
    }
}
