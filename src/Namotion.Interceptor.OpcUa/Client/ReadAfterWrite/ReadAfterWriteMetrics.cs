using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;

/// <summary>
/// Thread-safe metrics for read-after-write operations.
/// </summary>
internal sealed class ReadAfterWriteMetrics : IResettableMetrics
{
    private long _scheduled;
    private long _executed;
    private long _skipped;
    private long _coalesced;
    private long _failed;

    /// <summary>
    /// Gets the total number of read-after-writes scheduled.
    /// </summary>
    public long Scheduled => Interlocked.Read(ref _scheduled);

    /// <summary>
    /// Gets the total number of read-after-writes successfully executed.
    /// </summary>
    public long Executed => Interlocked.Read(ref _executed);

    /// <summary>
    /// Gets the number of read-back values that were discarded because the property had already moved
    /// on to something the read-back cannot rank above.
    /// </summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    /// <summary>
    /// Gets the number of scheduled reads that were coalesced (replaced by subsequent writes).
    /// </summary>
    public long Coalesced => Interlocked.Read(ref _coalesced);

    /// <summary>
    /// Gets the number of failed read-after-write operations.
    /// </summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>
    /// Records a new scheduled read-after-write.
    /// </summary>
    public void RecordScheduled() => Interlocked.Increment(ref _scheduled);

    /// <summary>
    /// Records a coalesced read (existing pending read replaced by new one).
    /// </summary>
    public void RecordCoalesced() => Interlocked.Increment(ref _coalesced);

    /// <summary>
    /// Records failed read-after-write operations.
    /// </summary>
    /// <param name="count">Number of reads that failed (must be non-negative).</param>
    internal void RecordFailed(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _failed, count);
        }
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"Scheduled={Scheduled}, Executed={Executed}, Skipped={Skipped}, Coalesced={Coalesced}, Failed={Failed}";

    /// <summary>
    /// Records successful execution of read-after-writes.
    /// </summary>
    /// <param name="count">Number of reads successfully executed (must be non-negative).</param>
    internal void RecordExecuted(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _executed, count);
        }
    }

    /// <summary>
    /// Records read-back values that were discarded as superseded.
    /// </summary>
    /// <param name="count">Number of discarded read-backs (must be non-negative).</param>
    internal void RecordSkipped(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _skipped, count);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        Interlocked.Exchange(ref _executed, 0);
        Interlocked.Exchange(ref _skipped, 0);
        Interlocked.Exchange(ref _coalesced, 0);
        Interlocked.Exchange(ref _failed, 0);
    }
}
