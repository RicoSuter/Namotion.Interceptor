namespace Namotion.Interceptor.Tracking.Lifecycle;

internal sealed class ReachabilityWalk(OwnershipGraph graph) {
    public bool IsAnchorReachable(IInterceptorSubject start, IInterceptorSubject? excluded, bool includeProtectors = false) =>
        graph.IsReachableFromRoot(start, excluded, includeProtectors);
}
