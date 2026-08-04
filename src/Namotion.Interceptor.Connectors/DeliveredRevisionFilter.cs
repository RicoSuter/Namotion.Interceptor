using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Remembers the newest commit already delivered for each property, so a change that a later commit
/// has already superseded is dropped instead of overwriting the source with a stale value.
///
/// Merging a flush batch only sees one batch. A change is enqueued after its commit and outside the
/// subject lock, so a writer preempted between the two can land revision 8 in the batch after the one
/// that carried revision 10, and the source would end up holding the older value. Nothing bounds how
/// long that preemption lasts, so no amount of buffering closes it; remembering what already went out
/// does.
///
/// State ages out through two generations rather than being evicted per property. Entries move to the
/// previous generation on rotation and are promoted back on the next hit, so anything still being
/// written survives while anything that has gone quiet falls out. That bounds memory at twice the
/// rotation threshold without needing to decide whether a property is still live: the obvious signal,
/// the processor's own property filter, is documented as transiently false while a subject is
/// momentarily unregistered during a structural mutation, and treating that as "gone" would drop a
/// baseline that is still needed.
///
/// The window is what the guarantee is bounded by, and it matches the shape of the problem: an
/// inversion comes from a thread preempted between committing and enqueuing, so a straggler is always
/// recent. A property that has not been written for a whole rotation cannot have one in flight.
///
/// Every entry point is synchronized, because this is genuinely reached from two threads at once. A
/// buffered processor records echoes and answers write-back questions on its dequeue thread while its
/// flush task suppresses an outbound batch, and the two touch the same map. An earlier version of this
/// class asserted the opposite, that the buffered and immediate modes made the callers mutually
/// exclusive. That was wrong: the echo bookkeeping sits ahead of the mode check and therefore runs in
/// both modes, on the dequeue thread, while only the flush path moved to the flush thread.
/// </summary>
internal sealed class DeliveredRevisionFilter
{
    // Two generations of this, so memory is bounded at twice the threshold. Large enough that a
    // property written even rarely within a busy window survives rotation.
    private const int RotationThreshold = 4096;

    // Guards both generations. A leaf lock: nothing below it touches a subject, invokes a callback or
    // performs I/O, so it cannot participate in a lock cycle. In particular the subject's SyncRoot is
    // released before a change is ever enqueued, so no caller reaches here holding it.
    private readonly Lock _gate = new();

    // WrittenOut records whether the newest commit for this property was sent to the source by this
    // processor, as opposed to already being there because the source sent or confirmed it. That is
    // what tells a transaction confirmation whether the source may have been overwritten since.
    private Dictionary<PropertyReference, (long Revision, bool WrittenOut)> _current = new(PropertyReference.Comparer);
    private Dictionary<PropertyReference, (long Revision, bool WrittenOut)> _previous = new(PropertyReference.Comparer);

    /// <summary>
    /// Returns whether the change should be delivered, recording it as the newest delivered commit for
    /// its property when it should. Used by the immediate path, which has a single change rather than a
    /// batch; see <see cref="SuppressDelivered"/> for the buffered one.
    /// </summary>
    public bool TryAdmit(in SubjectPropertyChange change)
    {
        if (change.Revision == 0)
        {
            // Orders against nothing, so nothing can establish that it is superseded. Delivered, and
            // deliberately not recorded: a recorded 0 could never suppress anything anyway. Reads only
            // the caller's own struct, so it needs no lock.
            return true;
        }

        lock (_gate)
        {
            return TryAdmitCore(in change);
        }
    }

    /// <summary>
    /// Drops the changes an earlier batch already superseded, compacting the survivors into the front of
    /// the span and returning how many were kept. The caller owns clearing whatever is left past that
    /// prefix.
    /// </summary>
    /// <remarks>
    /// A batch is admitted under one lock acquisition rather than one per change, which is both cheaper
    /// and stronger: the whole batch is decided against a single snapshot of the delivered state, so an
    /// echo arriving mid-batch cannot suppress one change of a batch and not another.
    /// </remarks>
    public int SuppressDelivered(Span<SubjectPropertyChange> survivors)
    {
        lock (_gate)
        {
            var kept = 0;
            for (var index = 0; index < survivors.Length; index++)
            {
                ref readonly var survivor = ref survivors[index];
                if (!TryAdmitCore(in survivor))
                {
                    continue;
                }

                if (kept != index)
                {
                    survivors[kept] = survivor;
                }

                kept++;
            }

            return kept;
        }
    }

    /// <summary>
    /// Whether the newest commit delivered for this property was written out by this processor. A
    /// transaction confirmation uses it to decide whether the source still holds what the transaction
    /// wrote: if this processor wrote to the property since, that write may have reached the source
    /// after the transaction's and left an older value behind, which only sending the confirmation
    /// out can repair.
    /// </summary>
    public bool WasWrittenOut(in PropertyReference property)
    {
        lock (_gate)
        {
            if (_current.TryGetValue(property, out var entry))
            {
                return entry.WrittenOut;
            }

            return _previous.TryGetValue(property, out entry) && entry.WrittenOut;
        }
    }

    /// <summary>
    /// Advances the baseline for a change the processor handles without writing it out, which means an
    /// echo of the source's own value. The source already holds that value at that revision, so leaving
    /// the baseline behind would let a local commit that predates the echo be admitted after it and
    /// overwrite the newer value the source just sent.
    /// </summary>
    public void RecordDelivered(in SubjectPropertyChange change)
    {
        if (change.Revision == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (IsSuperseded(change.Property, change.Revision))
            {
                return;
            }

            // Not written out by us: the source already holds this value, having sent or confirmed it.
            Record(change.Property, change.Revision, writtenOut: false);
        }
    }

    private bool TryAdmitCore(in SubjectPropertyChange change)
    {
        if (change.Revision == 0)
        {
            return true;
        }

        if (IsSuperseded(change.Property, change.Revision))
        {
            return false;
        }

        // A confirmation being written back leaves the source holding that same confirmed value, so it
        // is not an overwrite that a later confirmation would need to repair.
        Record(change.Property, change.Revision, writtenOut: change.Origin.Kind != ChangeOriginKind.Confirmed);
        return true;
    }

    private bool IsSuperseded(in PropertyReference property, long revision)
    {
        if (_current.TryGetValue(property, out var entry))
        {
            return revision <= entry.Revision;
        }

        return _previous.TryGetValue(property, out entry) && revision <= entry.Revision;
    }

    private void Record(in PropertyReference property, long revision, bool writtenOut)
    {
        // Always into the current generation, which is also what promotes an entry found in the
        // previous one and keeps an actively written property from ageing out.
        _current[property] = (revision, writtenOut);

        if (_current.Count > RotationThreshold)
        {
            Rotate();
        }
    }

    private void Rotate()
    {
        // The retired generation is cleared and reused, so rotation allocates nothing and drops every
        // reference it held, including subjects that have since been detached.
        (_previous, _current) = (_current, _previous);
        _current.Clear();
    }
}
