using System.Collections.Immutable;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors;

/// <summary>Consumer-facing entry points for source monitoring.</summary>
public static class SourceMonitoringExtensions
{
    /// <summary>
    /// Gets the property's synchronization state, derived from its owning source with no per-property storage.
    /// </summary>
    /// <remarks>
    /// Only fully meaningful once the branch containing the property has been awaited through
    /// WaitForSynchronizationAsync: before claiming has happened, Unclaimed cannot be distinguished
    /// from not-yet-claimed. After a claim it reports Connecting, so "will sync, still loading" is
    /// already distinguishable from "no source" even before the wait completes.
    /// </remarks>
    public static SourceState GetSourceState(this PropertyReference property)
    {
        return property.TryGetSource(out var source) ? source.State : SourceState.Unclaimed;
    }

    /// <summary>
    /// Adds source monitoring to this context. Call it on the TREE ROOT context: a service added to
    /// a subtree context is invisible to the root and to sibling subtrees, because context fallbacks
    /// point child to parent and never sideways, so a subtree-placed monitor fragments the tree.
    /// Implies WithParents, which the branch-scoped wait needs.
    /// </summary>
    public static IInterceptorSubjectContext WithSourceMonitoring(this IInterceptorSubjectContext context)
    {
        context.TryAddService(() => new SourceMonitor(), _ => true);
        return context.WithParents();
    }

    /// <summary>
    /// Resolves the single reachable monitor.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable, or more than one is.</exception>
    public static SourceMonitor GetSourceMonitor(this IInterceptorSubjectContext context)
    {
        var monitors = context.GetSourceMonitors();
        return monitors.Length switch
        {
            1 => monitors[0],
            0 => throw new InvalidOperationException(
                "No SourceMonitor is reachable from this context. Call WithSourceMonitoring() on the tree root context."),
            _ => throw new InvalidOperationException(
                $"{monitors.Length} SourceMonitor instances are reachable from this context. " +
                "Combining them is a decision for the call site: use GetServices<SourceMonitor>() and choose explicitly.")
        };
    }

    internal static ImmutableArray<SourceMonitor> GetSourceMonitors(this IInterceptorSubjectContext context)
    {
        return context.GetServices<SourceMonitor>();
    }
}
