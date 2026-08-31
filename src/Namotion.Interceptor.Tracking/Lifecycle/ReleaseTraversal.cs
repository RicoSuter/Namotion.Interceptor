namespace Namotion.Interceptor.Tracking.Lifecycle;

internal sealed class ReleaseTraversal(LifecycleNotifier notifier, OwnershipGraph graph) {
    public OwnershipGraph.PreparedTopologyChange Prepare(IInterceptorSubject subject, Interceptors.InterceptorExecutor executor) =>
        graph.PrepareDetach(subject, executor, notifier);
}
