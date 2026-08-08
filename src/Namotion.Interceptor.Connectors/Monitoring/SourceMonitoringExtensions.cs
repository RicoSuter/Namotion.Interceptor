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
    /// from not-yet-claimed. After a claim it reports Synchronizing, so "will sync, still loading" is
    /// already distinguishable from "no source" even before the wait completes.
    /// </remarks>
    public static SourceState GetSourceState(this PropertyReference property)
    {
        return property.TryGetSource(out var source) ? source.State : SourceState.Unclaimed;
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
        ExceptionAggregation.ForEach(
            ResolveMonitorsOrThrow(context), monitor => monitor.CompleteSourceRegistration());
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
        public void Dispose() => ExceptionAggregation.ForEach(disposables, hold => hold.Dispose());
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
