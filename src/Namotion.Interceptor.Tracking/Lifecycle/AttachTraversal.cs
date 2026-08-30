using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

internal sealed class AttachTraversal(LifecycleNotifier notifier, OwnershipGraph graph)
{
    public void SeedChildrenIfNeeded(
        IInterceptorSubject subject,
        Dictionary<IInterceptorSubject, OwnershipReservationToken>? reservations = null) =>
        graph.SeedAndAttachChildren(subject, reservations, !graph.AreSnapshotsSeeded(subject), notifier);

    public void SeedAndAttachChildren(
        IInterceptorSubject subject,
        Dictionary<IInterceptorSubject, OwnershipReservationToken>? reservations = null,
        bool seed = true) =>
        graph.SeedAndAttachChildren(subject, reservations, seed, notifier);

    public void AttachEdge(
        IInterceptorSubject subject,
        PropertyReference property,
        int subjectOrdinal,
        object? index,
        Dictionary<IInterceptorSubject, OwnershipReservationToken>? reservations = null) =>
        graph.AttachEdge(subject, property, subjectOrdinal, index, reservations, notifier);

    public void AttachRoot(IInterceptorSubject subject) => graph.AttachRoot(subject, notifier);
}
