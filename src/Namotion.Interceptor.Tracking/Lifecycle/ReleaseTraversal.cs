namespace Namotion.Interceptor.Tracking.Lifecycle;

internal sealed class ReleaseTraversal(LifecycleNotifier notifier, OwnershipGraph graph) {
    public OwnershipGraph.PreparedTopologyChange Prepare(IInterceptorSubject subject) => graph.PrepareDetach(subject, notifier);
}
