using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Modbus.Polling;

internal sealed class ModbusPollingMetrics : IResettableMetrics
{
    private long _totalPolls;
    private long _failedBatches;
    private long _lastPollDurationTicks;
    private long _lastPollTimeUtcTicks;
    private int _batchCount;
    private int _unavailableProperties;

    public long TotalPolls => Interlocked.Read(ref _totalPolls);

    public long FailedBatches => Interlocked.Read(ref _failedBatches);

    public int BatchCount => Volatile.Read(ref _batchCount);

    public int UnavailableProperties => Volatile.Read(ref _unavailableProperties);

    public DateTimeOffset? LastPollTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastPollTimeUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public TimeSpan? LastPollDuration => LastPollTime is null
        ? null
        : TimeSpan.FromTicks(Interlocked.Read(ref _lastPollDurationTicks));

    public void RecordPoll(TimeSpan duration, DateTimeOffset time)
    {
        Interlocked.Exchange(ref _lastPollDurationTicks, duration.Ticks);
        Interlocked.Exchange(ref _lastPollTimeUtcTicks, time.UtcTicks);
        Interlocked.Increment(ref _totalPolls);
    }

    public void RecordFailedBatch() => Interlocked.Increment(ref _failedBatches);

    public void SetPlan(int batchCount, int unavailableProperties)
    {
        Volatile.Write(ref _batchCount, batchCount);
        Volatile.Write(ref _unavailableProperties, unavailableProperties);
    }

    /// <summary>
    /// Resets the cumulative counters. The plan gauges and the last poll time and duration are left alone.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalPolls, 0);
        Interlocked.Exchange(ref _failedBatches, 0);
    }
}
