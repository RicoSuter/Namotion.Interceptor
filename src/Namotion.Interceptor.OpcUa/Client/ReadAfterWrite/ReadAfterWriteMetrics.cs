namespace Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;

/// <summary>
/// Thread-safe metrics for read-after-write operations.
/// </summary>
internal sealed class ReadAfterWriteMetrics
{
    private long _scheduled;
    private long _executed;
    private long _coalesced;
    private long _failed;

    /// <summary>
    /// Gets the total number of read-after-writes scheduled.
    /// </summary>
    public long Scheduled => Volatile.Read(ref _scheduled);

    /// <summary>
    /// Gets the total number of read-after-writes successfully executed.
    /// </summary>
    public long Executed => Volatile.Read(ref _executed);

    /// <summary>
    /// Gets the number of scheduled reads that were coalesced (replaced by subsequent writes).
    /// </summary>
    public long Coalesced => Volatile.Read(ref _coalesced);

    /// <summary>
    /// Gets the number of failed read-after-write operations.
    /// </summary>
    public long Failed => Volatile.Read(ref _failed);

    /// <summary>
    /// Records a new scheduled read-after-write.
    /// </summary>
    public void RecordScheduled() => Interlocked.Increment(ref _scheduled);

    /// <summary>
    /// Records a coalesced read (existing pending read replaced by new one).
    /// </summary>
    public void RecordCoalesced() => Interlocked.Increment(ref _coalesced);

    /// <summary>
    /// Records successful execution of read-after-writes.
    /// </summary>
    /// <param name="count">Number of reads successfully executed.</param>
    public void RecordExecuted(int count) => Interlocked.Add(ref _executed, count);

    /// <summary>
    /// Records a failed read-after-write operation.
    /// </summary>
    public void RecordFailed() => Interlocked.Increment(ref _failed);

    /// <inheritdoc/>
    public override string ToString() =>
        $"Scheduled={Scheduled}, Executed={Executed}, Coalesced={Coalesced}, Failed={Failed}";
}
