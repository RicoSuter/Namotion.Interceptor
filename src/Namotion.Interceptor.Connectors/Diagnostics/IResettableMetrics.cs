namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Implemented by metrics objects that a connector owns outside its <c>ConnectorMetrics</c> and that
/// must still take part in the counter reset performed by <c>ConnectorMetrics.MarkStarted</c>.
/// </summary>
/// <remarks>
/// Metrics are hoisted out of short-lived components so their totals survive a reconnect. That is
/// what makes the counters honest, and it is also why they cannot be reached by resetting
/// <c>ConnectorMetrics</c> alone. Register them with <c>ConnectorMetrics.RegisterResettable</c> so
/// the epoch stays consistent across every <c>Total</c> counter a connector reports.
/// </remarks>
public interface IResettableMetrics
{
    /// <summary>
    /// Resets every cumulative counter to zero. Gauges and last-event timestamps are left alone.
    /// </summary>
    void Reset();
}
