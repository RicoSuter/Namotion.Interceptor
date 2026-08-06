using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>Consumer-facing entry points for source monitoring.</summary>
public static class SourceMonitoringExtensions
{
    private const string NoMonitorMessage =
        "No SourceMonitor is reachable from this context. Call WithSourceMonitoring() on the tree root context.";
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

            // Seed membership for every subject already attached before the monitor became a
            // handler: otherwise a subject constructed earlier, including the root, stays a
            // permanent non-member and CurrentState reports Unclaimed for its properties forever,
            // with no later event to fix it. Seeding runs AFTER registering the handler above, under
            // the same lock ForEachAttachedSubject holds across attach and detach: a concurrent
            // attach is therefore caught by the now-registered handler rather than missed by the
            // seed, and a concurrent detach cannot leave a phantom member, because the walk and the
            // detach cannot interleave. WithParents (above) guarantees a LifecycleInterceptor is
            // reachable, so its absence here means a broken invariant, not a configuration this can
            // silently tolerate.
            var lifecycleInterceptor = context.TryGetLifecycleInterceptor()
                ?? throw new InvalidOperationException(
                    "No LifecycleInterceptor is reachable from this context, even though " +
                    "WithSourceMonitoring calls WithParents(), which implies WithLifecycle(). Call " +
                    "WithLifecycle() (or WithParents()) on this context before WithSourceMonitoring().");
            lifecycleInterceptor.ForEachAttachedSubject(monitor.SeedMembership);

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
            0 => throw new InvalidOperationException(NoMonitorMessage),
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
        List<Exception>? exceptions = null;

        foreach (var monitor in monitors)
        {
            try
            {
                monitor.CompleteSourceRegistration();
            }
            catch (Exception ex)
            {
                (exceptions ??= []).Add(ex);
            }
        }

        ExceptionAggregation.ThrowIfAny(exceptions);
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
            throw new InvalidOperationException(NoMonitorMessage);
        }

        return monitors;
    }

    private sealed class CompositeDisposable(IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            List<Exception>? exceptions = null;

            foreach (var disposable in disposables)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    (exceptions ??= []).Add(ex);
                }
            }

            ExceptionAggregation.ThrowIfAny(exceptions);
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

        // Bridges the host's logging provider into the context, the same way WithHostedServices
        // wires HostedServiceHandler's logger from DI: the context is configured before the host is
        // built, so without this the monitor's lazy logger resolver (see WithSourceMonitoring())
        // never finds an ILoggerFactory and every wait-engine warning is a silent no-op.
        services.AddHostedService(serviceProvider =>
        {
            context.TryAddService(serviceProvider.GetRequiredService<ILoggerFactory>, _ => true);
            return new SourceRegistrationGate(
                context, serviceProvider.GetRequiredService<IHostApplicationLifetime>());
        });

        return context;
    }

    /// <summary>
    /// Waits until every source that can claim into this subject's branch has completed its initial
    /// load. The subject IS the scope, so waiting on the tree root means the whole tree.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable from the subject's context.</exception>
    public static Task WaitForSynchronizationAsync(
        this IInterceptorSubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var monitors = ResolveMonitorsOrThrow(subject.Context);
        if (monitors.Length == 1)
        {
            return monitors[0].WaitForSynchronizationAsync(subject, cancellationToken);
        }

        return Task.WhenAll(monitors.Select(
            monitor => monitor.WaitForSynchronizationAsync(subject, cancellationToken)));
    }
}
