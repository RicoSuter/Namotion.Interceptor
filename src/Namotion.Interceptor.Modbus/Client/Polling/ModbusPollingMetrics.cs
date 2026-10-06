using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Modbus.Client.Polling;

internal sealed class ModbusPollingMetrics : IResettableMetrics
{
    private long _totalPolls;
    private long _totalFailedRequests;
    private long _lastPollDurationTicks = -1;
    private long _lastPollTimeUtcTicks;
    private int _batchCount;
    private int _unavailablePropertyCount;

    public long TotalPolls => Interlocked.Read(ref _totalPolls);

    public long TotalFailedRequests => Interlocked.Read(ref _totalFailedRequests);

    public int BatchCount => Volatile.Read(ref _batchCount);

    public int UnavailablePropertyCount => Volatile.Read(ref _unavailablePropertyCount);

    public DateTimeOffset? LastPollTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastPollTimeUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public TimeSpan? LastPollDuration
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastPollDurationTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    /// <summary>
    /// Records a completed poll cycle.
    /// </summary>
    public void RecordPoll(TimeSpan duration, DateTimeOffset time, bool hasReadData)
    {
        Interlocked.Exchange(ref _lastPollDurationTicks, duration.Ticks);
        if (hasReadData)
        {
            Interlocked.Exchange(ref _lastPollTimeUtcTicks, time.UtcTicks);
        }

        Interlocked.Increment(ref _totalPolls);
    }

    public void RecordFailedRequest() => Interlocked.Increment(ref _totalFailedRequests);

    public void SetPlan(int batchCount, int unavailablePropertyCount)
    {
        Volatile.Write(ref _batchCount, batchCount);
        Volatile.Write(ref _unavailablePropertyCount, unavailablePropertyCount);
    }

    /// <summary>
    /// Resets the cumulative counters. The plan gauges and the last poll time and duration are left alone.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalPolls, 0);
        Interlocked.Exchange(ref _totalFailedRequests, 0);
    }
}
