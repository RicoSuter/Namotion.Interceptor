namespace Namotion.Interceptor.Tracking.Lifecycle;

internal sealed class ReleaseTraversal(LifecycleNotifier notifier, OwnershipGraph graph)
{
    public void RemoveEdge(
        IInterceptorSubject subject,
        PropertyReference property,
        int subjectOrdinal,
        object? index) =>
        graph.RemoveEdge(subject, property, subjectOrdinal, index, notifier);

    public void ReleaseRoot(IInterceptorSubject subject) => graph.ReleaseRoot(subject, notifier);
}
