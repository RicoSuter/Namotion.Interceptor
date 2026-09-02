using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Owns every outbound change a <see cref="ChangeQueueProcessor"/> has accepted, from the moment it is
/// buffered to the moment its outcome is known.
/// </summary>
/// <remarks>
/// The invariant this type exists to hold: an accepted change is owned by exactly one of the buffered
/// queue, one active delivery, or the terminal drop count. It is never in two of them and never in none.
/// <para>
/// Two boundaries scope it. When the write handler owns delivery, <see cref="MergedDeliveryAdmission"/>
/// is null and a change leaves this accounting for good when the pump hands it over: only the buffered
/// queue and terminal closure are kept here. And a flush holds dequeued changes in its scratch buffer
/// until the merger asks for admission, so a merge failure inside that window loses them to all three
/// places; the flush gate keeps that window single-threaded.
/// </para>
/// <para>
/// Delivery admission is a single integer: 0 means idle, a positive value is the size of the one delivery
/// in flight, and <see cref="ClosedDelivery"/> is terminal. Admission requires idle, so two deliveries
/// cannot overlap and a batch that finishes after close cannot reopen admission. The lock exists only to
/// make a queue transition atomic with the admission transition, which is what stops a cancelled batch
/// being requeued into a queue that close has already drained.
/// </para>
/// </remarks>
internal sealed class OutboundDeliveryLedger
{
    private const int ClosedDelivery = -1;

    // Lock-free for the subscription thread that fills it.
    private readonly ConcurrentQueue<SubjectPropertyChange> _changes = new();

    // Held only across a paired queue and admission transition, never across an await or a callback.
    private readonly Lock _ownership = new();

    private readonly int? _maxQueueDepth;
    private readonly Action<long>? _dropHandler;
    private readonly ILogger _logger;
    private readonly TimeSpan _terminalBound;

    private long _dropCount;
    private int _deliveryState;

    public OutboundDeliveryLedger(
        int? maxQueueDepth,
        Action<long>? dropHandler,
        ILogger logger,
        TimeSpan terminalBound,
        bool tracksDeliveryOutcome)
    {
        _maxQueueDepth = maxQueueDepth;
        _dropHandler = dropHandler;
        _logger = logger;
        _terminalBound = terminalBound;
        MergedDeliveryAdmission = tracksDeliveryOutcome ? TryAdmitOrCountTerminal : null;
    }

    /// <summary>
    /// The admission callback a flush hands the merger, or null when the write handler owns delivery
    /// and no outcome is accounted here. Allocated once so a flush does not.
    /// </summary>
    public Func<int, bool>? MergedDeliveryAdmission { get; }

    /// <summary>Total changes this ledger has accounted for as lost, for any reason.</summary>
    public long DropCount => Interlocked.Read(ref _dropCount);

    /// <summary>Changes buffered right now. Approximate while the pump is running.</summary>
    public int Depth => _changes.Count;

    /// <summary>
    /// Takes ownership of a buffered change, dropping the oldest ones when a bound is set and exceeded.
    /// Trimming is best effort: a concurrent flush may drain below the bound first, dropping fewer.
    /// </summary>
    public void Enqueue(in SubjectPropertyChange change)
    {
        _changes.Enqueue(change);
        if (_maxQueueDepth is not int maxQueueDepth || _changes.Count <= maxQueueDepth)
        {
            return;
        }

        var droppedCount = 0L;
        while (_changes.Count > maxQueueDepth && _changes.TryDequeue(out _))
        {
            Interlocked.Increment(ref _dropCount);
            droppedCount++;
        }

        if (droppedCount > 0)
        {
            InvokeDropHandler(droppedCount);
        }
    }

    /// <summary>Hands a buffered change to the flush that is about to deliver it.</summary>
    public bool TryDequeue(out SubjectPropertyChange change) => _changes.TryDequeue(out change);

    /// <summary>
    /// Claims delivery for <paramref name="count"/> changes. Fails while another delivery is in flight or
    /// once ownership has closed.
    /// </summary>
    private bool TryAdmit(int count) => Interlocked.CompareExchange(ref _deliveryState, count, 0) == 0;

    /// <summary>
    /// Claims delivery, and accounts for the batch as terminally lost when the claim is refused, so a
    /// caller that is about to discard the batch never has to remember to count it.
    /// </summary>
    public bool TryAdmitOrCountTerminal(int count)
    {
        var admitted = TryAdmit(count);
        if (!admitted)
        {
            CountTerminalDrops(count);
        }

        return admitted;
    }

    /// <summary>Releases a delivery that the handler completed. A no-op once ownership has closed.</summary>
    public void CompleteDelivery(int count) => Interlocked.CompareExchange(ref _deliveryState, 0, count);

    /// <summary>
    /// Returns a cancelled delivery to the queue so a later flush can retry it. Does nothing when close
    /// already claimed the batch, which is what keeps it from landing in an already drained queue.
    /// </summary>
    public void ReturnCancelledDelivery(ReadOnlySpan<SubjectPropertyChange> changes, int count)
    {
        lock (_ownership)
        {
            if (Interlocked.CompareExchange(ref _deliveryState, 0, count) != count)
            {
                return;
            }

            foreach (var change in changes)
            {
                _changes.Enqueue(change);
            }
        }
    }

    /// <summary>
    /// Accounts for a delivery the handler failed. Returns false when close already claimed the batch and
    /// counted it, so the caller neither counts nor reports it twice.
    /// </summary>
    public bool TryCountFailedDelivery(int count)
    {
        lock (_ownership)
        {
            if (Interlocked.CompareExchange(ref _deliveryState, 0, count) != count)
            {
                return false;
            }

            Interlocked.Add(ref _dropCount, count);
        }

        InvokeDropHandler(count);
        return true;
    }

    /// <summary>
    /// Closes ownership for good, claims everything still outstanding (the delivery in flight plus
    /// everything still buffered) and accounts for all of it as terminally lost. One method, so a
    /// closed ledger can never have claimed changes that were not counted.
    /// </summary>
    public void CloseAndCountTerminalDrops() => CountTerminalDrops(CloseAndDrain());

    /// <remarks>
    /// Under the lock, so a cancellation requeue or a failure accounting that is mid-transition settles
    /// first and is observed rather than missed.
    /// </remarks>
    private int CloseAndDrain()
    {
        lock (_ownership)
        {
            var count = Math.Max(0, Interlocked.Exchange(ref _deliveryState, ClosedDelivery));
            while (_changes.TryDequeue(out _))
            {
                count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Accounts for changes whose delivery outcome will never be known locally. Reporting is dispatched
    /// off the caller's thread, because the caller is a teardown path that must stay bounded and both the
    /// drop handler and the logger are consumer supplied.
    /// </summary>
    private void CountTerminalDrops(int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _dropCount, count);
        _ = Task.Run(() =>
        {
            InvokeDropHandler(count);
            try
            {
                _logger.LogWarning(
                    "Gave up waiting after {Timeout} for {Count} changes to be written while stopping. " +
                    "A write handler may already have completed them remotely or may still complete them.",
                    _terminalBound,
                    count);
            }
            catch
            {
                // Reporting is best effort after ownership has already been settled.
            }
        });
    }

    private void InvokeDropHandler(long count)
    {
        try { _dropHandler?.Invoke(count); } catch { }
    }
}
