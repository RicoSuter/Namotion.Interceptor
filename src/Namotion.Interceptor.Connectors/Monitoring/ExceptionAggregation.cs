using System.Runtime.ExceptionServices;

namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>
/// Shared collect-and-rethrow tail for loops that isolate each iteration's exception from the
/// others, so one failure does not skip the remaining iterations: SourceMonitor's wait
/// re-evaluation, SourceMonitoringExtensions.CompleteSourceRegistration across monitors, and
/// CompositeDisposable's hold release.
/// </summary>
internal static class ExceptionAggregation
{
    /// <summary>
    /// Throws the single collected exception directly, or an <see cref="AggregateException"/> when
    /// more than one was collected. Does nothing when <paramref name="exceptions"/> is null or empty.
    /// </summary>
    internal static void ThrowIfAny(List<Exception>? exceptions)
    {
        if (exceptions is { Count: 1 })
        {
            // ExceptionDispatchInfo preserves the original stack trace; a bare `throw exceptions[0]`
            // resets it to this rethrow site, hiding where the exception actually came from.
            ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
        }

        if (exceptions is { Count: > 1 })
        {
            throw new AggregateException(exceptions);
        }
    }
}
