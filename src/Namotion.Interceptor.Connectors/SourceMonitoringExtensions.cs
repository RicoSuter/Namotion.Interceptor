using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

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
        // WithParents FIRST, so ParentTrackingHandler is registered before the monitor. Ordering
        // among lifecycle handlers is a stable topological sort, and registration order breaks ties.
        context.WithParents();

        context.TryAddService<SourceMonitor>(() =>
        {
            // Lazy logger: the context is configured before any logging provider exists. This is the
            // same Func<ILogger?> idiom HostedServiceHandler uses. Without it every warning the wait
            // engine emits is a silent no-op, and those warnings are the only thing distinguishing a
            // vacuous completion from a live tree.
            var monitor = new SourceMonitor(() =>
                context.TryGetService<ILoggerFactory>()?.CreateLogger<SourceMonitor>());
            context.AddService<ILifecycleHandler>(monitor);
            return monitor;
        }, _ => true);

        return context;
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

    /// <summary>
    /// Declares that source registration is complete on every reachable monitor. Idempotent.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable.</exception>
    public static void CompleteSourceRegistration(this IInterceptorSubjectContext context)
    {
        var monitors = ResolveMonitorsOrThrow(context);
        foreach (var monitor in monitors)
        {
            monitor.CompleteSourceRegistration();
        }
    }

    /// <summary>
    /// Blocks wait completion on every reachable monitor until the returned handle is disposed.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable.</exception>
    public static IDisposable DeferWaitCompletion(this IInterceptorSubjectContext context)
    {
        var monitors = ResolveMonitorsOrThrow(context);
        var holds = monitors.Select(monitor => monitor.DeferWaitCompletion()).ToArray();
        return new CompositeDisposable(holds);
    }

    private static ImmutableArray<SourceMonitor> ResolveMonitorsOrThrow(IInterceptorSubjectContext context)
    {
        var monitors = context.GetSourceMonitors();
        if (monitors.IsEmpty)
        {
            throw new InvalidOperationException(
                "No SourceMonitor is reachable from this context. Call WithSourceMonitoring() on the tree root context.");
        }

        return monitors;
    }

    private sealed class CompositeDisposable(IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
            {
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// Adds source monitoring and registers a hosted service that completes source registration when
    /// IHostApplicationLifetime.ApplicationStarted fires. Use this when every source is a
    /// DI-registered hosted service. Applications that create sources at runtime use the
    /// parameterless overload and call CompleteSourceRegistration themselves.
    /// </summary>
    public static IInterceptorSubjectContext WithSourceMonitoring(
        this IInterceptorSubjectContext context, IServiceCollection services)
    {
        context.WithSourceMonitoring();
        services.AddHostedService(serviceProvider => new SourceRegistrationGate(
            context, serviceProvider.GetRequiredService<IHostApplicationLifetime>()));
        return context;
    }
}
