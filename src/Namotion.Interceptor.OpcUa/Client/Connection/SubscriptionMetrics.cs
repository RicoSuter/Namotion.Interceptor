using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

/// <summary>
/// Thread-safe metrics for subscription notification handling. Owned by the source rather than by
/// <see cref="SubscriptionManager"/>, which is rebuilt on every connect attempt including failed
/// ones, so the totals survive a reconnect storm.
/// </summary>
internal sealed class SubscriptionMetrics : IResettableMetrics
{
    private long _skippedBadValues;

    /// <summary>
    /// Gets the number of notified values skipped because the server marked them Bad.
    /// </summary>
    public long SkippedBadValues => Interlocked.Read(ref _skippedBadValues);

    /// <summary>
    /// Records a notified value skipped because the server marked it Bad.
    /// </summary>
    public void RecordSkippedBadValue() => Interlocked.Increment(ref _skippedBadValues);

    /// <inheritdoc />
    public void Reset() => Interlocked.Exchange(ref _skippedBadValues, 0);
}
