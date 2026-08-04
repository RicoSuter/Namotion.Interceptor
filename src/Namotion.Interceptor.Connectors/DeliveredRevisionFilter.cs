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
/// State is bounded by graph membership: <see cref="RemoveSubject"/> drops a subject's entries when it
/// leaves the object graph. The key is a <see cref="PropertyReference"/>, which holds its subject
/// strongly, so without that eviction a detached subject and everything it roots would stay alive for
/// the processor's lifetime.
///
/// Eviction replaced an ageing scheme that retired whole generations once enough distinct properties
/// had been recorded. That bounded the entry count but not what those entries rooted, and it only
/// triggered for models with more distinct properties than its threshold, so the fixed-size models
/// most connectors serve never aged anything out at all. Graph membership is the signal that scheme
/// was approximating, so using it directly is smaller and exact: a live property keeps its baseline
/// for as long as it exists, with no window to reason about.
///
/// Every entry point is synchronized, because this is genuinely reached from three threads. A buffered
/// processor records echoes and answers write-back questions on its dequeue thread while its flush task
/// suppresses an outbound batch, and detach eviction arrives on whichever thread mutated the graph. An
/// earlier version asserted the opposite, that the buffered and immediate modes made the callers
/// mutually exclusive. That was wrong: the echo bookkeeping sits ahead of the mode check and so runs in
/// both modes on the dequeue thread, while only the flush path moved to the flush thread.
/// </summary>
internal sealed class DeliveredRevisionFilter
{
    // Safety valve, not a policy knob: eviction is what bounds this, and it needs a lifecycle
    // interceptor in the context to deliver detach events. A context configured without one still
    // accumulates one entry per distinct property ever written, so this caps that at a size no real
    // model reaches.
    private const int MaximumEntries = 100_000;

    // Guards the map. A leaf lock: nothing under it touches a subject, invokes a callback or performs
    // I/O, so it cannot participate in a cycle. The subject's SyncRoot is released before a change is
    // enqueued, and detach eviction takes this while the lifecycle interceptor holds its own lock, so
    // the order is always lifecycle then this, never the reverse.
    private readonly Lock _gate = new();

    // WrittenOut records whether the newest commit for this property was sent to the source by this
    // processor, as opposed to already being there because the source sent or confirmed it. That is
    // what tells a transaction confirmation whether the source may have been overwritten since.
    private readonly Dictionary<PropertyReference, (long Revision, bool WrittenOut)> _entries = new(PropertyReference.Comparer);

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
            return _entries.TryGetValue(property, out var entry) && entry.WrittenOut;
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

    /// <summary>
    /// Drops every baseline held for a subject that has left the object graph, releasing the subject
    /// itself along with them. A detached subject can have no straggler in flight worth suppressing,
    /// because nothing commits to it any more.
    /// </summary>
    public void RemoveSubject(IInterceptorSubject subject)
    {
        lock (_gate)
        {
            // Removing during enumeration is supported since .NET Core 3.0, so this needs no
            // intermediate list and allocates nothing.
            foreach (var entry in _entries)
            {
                if (ReferenceEquals(entry.Key.Subject, subject))
                {
                    _entries.Remove(entry.Key);
                }
            }
        }
    }

    private bool IsSuperseded(in PropertyReference property, long revision)
    {
        return _entries.TryGetValue(property, out var entry) && revision <= entry.Revision;
    }

    private void Record(in PropertyReference property, long revision, bool writtenOut)
    {
        if (_entries.Count >= MaximumEntries && !_entries.ContainsKey(property))
        {
            // Safety valve only; see the constant. Losing every baseline degrades to not filtering,
            // which is the behaviour before this filter existed, rather than to anything incorrect.
            _entries.Clear();
        }

        _entries[property] = (revision, writtenOut);
    }
}
