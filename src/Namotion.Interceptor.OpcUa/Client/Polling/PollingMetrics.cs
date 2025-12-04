namespace Namotion.Interceptor.OpcUa.Client.Polling;

/// <summary>
/// Thread-safe metrics collection for polling operations.
/// All operations use interlocked operations for thread-safety.
/// </summary>
internal sealed class PollingMetrics
{
    private long _totalReads;
    private long _failedReads;
    private long _valueChanges;
    private long _slowPolls;

    /// <summary>
    /// Gets the total number of successful read operations performed.
    /// </summary>
    public long TotalReads => Interlocked.Read(ref _totalReads);

    /// <summary>
    /// Gets the total number of failed read operations.
    /// </summary>
    public long FailedReads => Interlocked.Read(ref _failedReads);

    /// <summary>
    /// Gets the total number of value changes detected and processed.
    /// </summary>
    public long ValueChanges => Interlocked.Read(ref _valueChanges);

    /// <summary>
    /// Gets the total number of slow polls (poll duration exceeded polling interval).
    /// </summary>
    public long SlowPolls => Interlocked.Read(ref _slowPolls);

    /// <summary>
    /// Records a successful read operation.
    /// </summary>
    public void RecordRead()
    {
        Interlocked.Increment(ref _totalReads);
    }

    /// <summary>
    /// Records a failed read operation.
    /// </summary>
    public void RecordFailedRead()
    {
        Interlocked.Increment(ref _failedReads);
    }

    /// <summary>
    /// Records a value change.
    /// </summary>
    public void RecordValueChange()
    {
        Interlocked.Increment(ref _valueChanges);
    }

    /// <summary>
    /// Records a slow poll (poll duration exceeded interval).
    /// </summary>
    public void RecordSlowPoll()
    {
        Interlocked.Increment(ref _slowPolls);
    }

    /// <summary>
    /// Resets all metrics to zero.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalReads, 0);
        Interlocked.Exchange(ref _failedReads, 0);
        Interlocked.Exchange(ref _valueChanges, 0);
        Interlocked.Exchange(ref _slowPolls, 0);
    }
}
