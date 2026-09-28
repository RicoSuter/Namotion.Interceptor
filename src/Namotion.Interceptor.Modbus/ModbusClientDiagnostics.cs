using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Modbus.Polling;

namespace Namotion.Interceptor.Modbus;

/// <summary>
/// What a Modbus client source reports about its connection and polling.
/// </summary>
public sealed class ModbusClientDiagnostics : SourceDiagnostics
{
    internal ModbusClientDiagnostics(SourceMetrics metrics, ModbusPollingMetrics pollingMetrics)
        : base(metrics)
    {
        Polling = new ModbusPollingDiagnostics(pollingMetrics);
    }

    /// <summary>
    /// Gets the polling statistics.
    /// </summary>
    public ModbusPollingDiagnostics Polling { get; }
}

/// <summary>
/// Polling statistics of a Modbus client source.
/// </summary>
public sealed class ModbusPollingDiagnostics
{
    private readonly ModbusPollingMetrics _metrics;

    internal ModbusPollingDiagnostics(ModbusPollingMetrics metrics)
    {
        _metrics = metrics;
    }

    /// <summary>
    /// Gets the number of completed poll cycles since the source started or the diagnostics were last reset.
    /// </summary>
    public long TotalPolls => _metrics.TotalPolls;

    /// <summary>
    /// Gets the number of planned read requests answered with a Modbus exception response since the source started or the
    /// diagnostics were last reset. One-by-one re-reads of a rejected request and discovery reads are not counted.
    /// </summary>
    public long FailedBatches => _metrics.FailedBatches;

    /// <summary>
    /// Gets the number of read requests per poll cycle.
    /// </summary>
    public int BatchCount => _metrics.BatchCount;

    /// <summary>
    /// Gets the number of mappings the device rejected, which are not read until the next connect.
    /// </summary>
    public int UnavailableProperties => _metrics.UnavailableProperties;

    /// <summary>
    /// Gets the duration of the last completed poll cycle, or <c>null</c> before the first one.
    /// </summary>
    public TimeSpan? LastPollDuration => _metrics.LastPollDuration;

    /// <summary>
    /// Gets when the last poll cycle completed, or <c>null</c> before the first one.
    /// </summary>
    public DateTimeOffset? LastPollTime => _metrics.LastPollTime;
}
