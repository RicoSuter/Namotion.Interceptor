using System.Collections.Concurrent;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Remembers the newest commit already delivered for each property, so a change that a later commit
/// has already superseded is dropped instead of overwriting the source with a stale value.
///
/// A change is enqueued after its commit and outside the subject lock, so a writer preempted between the
/// two can land revision 8 in the batch after the one that carried revision 10, and the source would end
/// up holding the older value. Nothing bounds how long that preemption lasts, so no amount of buffering
/// closes it; remembering what already went out does.
///
/// The state lives on the subject, under a key this assembly owns, reached through the public property
/// data API. A processor-side map keyed by <see cref="PropertyReference"/> holds its subjects strongly,
/// so it needs eviction, eviction needs a liveness signal, and every available signal is either wrong
/// during structural mutation or arrives too late to stop an in-flight change re-inserting the entry.
/// Keyed on the subject, the baseline is collected with the subject and none of that is needed.
///
/// The retention is therefore reversed rather than removed: a slot holds its source, so a subject holds
/// the connector that delivers it. That is why <see cref="Release"/> exists and why the processor calls
/// it on disposal. It is not optional. A connector can be recreated against a live graph, for example a
/// HomeBlaze OPC UA server rebuilt on every configuration save, and without release each rebuild would
/// strand a dead connector reachable from the graph and add an entry to the per-property slot scan.
///
/// The price is a property data lookup per consulted change rather than a dictionary probe, and one
/// small object per property this connector delivers. That is deliberate: the core library stays unaware
/// of connector concerns, and the connector stops holding references to a graph it does not own.
///
/// Per source, because the state is now shared storage rather than a field of one processor: without the
/// source dimension, two processors serving one property would suppress each other's deliveries. Slots
/// are found by reference, and a property is served by one or two sources in practice.
///
/// This requires the source to outlive the subjects it delivers, because a slot holds it and nothing
/// removes one. Every construction site passes a long-lived connector, and each keeps a single live
/// processor across reconnects, so the array holds one entry per protocol. A source recreated per
/// connection or per tenant would grow it without bound and turn the scan linear, so that is the
/// invariant to preserve if a new caller appears.
///
/// Free of locks. Each slot is a single packed long, advanced with a compare-and-exchange that only ever
/// moves a revision forward, so the dequeue thread and the flush task can consult the same property
/// without coordinating.
/// </summary>
internal sealed class DeliveredRevisionFilter
{
    // Short by convention: this is hashed on every consulted change, and string hash codes are not
    // cached, so key length is per-call work. Matches the "ni.*" keys the tracking layer uses.
    private const string DeliveredRevisionKey = "ni.drev";

    private readonly object _source;

    // The slot holders this filter has put a slot into, so disposal can take them back out. Holding
    // these retains no graph: a holder has no reference to its subject, only the subject's property data
    // has a reference to the holder. One entry per property this connector has delivered, added on first
    // touch rather than per change.
    private readonly ConcurrentDictionary<DeliveredRevisionSlots, byte> _touchedSlots = new();
    private volatile bool _released;

    public DeliveredRevisionFilter(object? source = null)
    {
        // Null only in tests and in processors constructed without a source; the instance itself is then
        // the identity, which keeps slot lookup total without a null branch on the hot path.
        _source = source ?? this;
    }

    /// <summary>
    /// Returns whether the change should be delivered, recording it as the newest delivered commit for
    /// its property when it should.
    /// </summary>
    public bool TryAdmit(in SubjectPropertyChange change)
    {
        if (change.Revision <= 0)
        {
            // Orders against nothing, so nothing can establish that it is superseded. Delivered, and
            // deliberately not recorded: a recorded 0 could never suppress anything anyway. Negative
            // revisions are outside the contract too (a committed write starts at 1) and take the same
            // route rather than being dropped, matching how ChangeMerger treats them and erring toward
            // delivering rather than silently discarding.
            return true;
        }

        // A confirmation being written back leaves the source holding that same confirmed value, so it
        // is not an overwrite that a later confirmation would need to repair.
        return TryAdvance(change.Property, change.Revision,
            writtenOut: change.Origin.Kind != ChangeOriginKind.Confirmed);
    }

    /// <summary>
    /// Drops the changes an earlier batch already superseded, compacting the survivors into the front of
    /// the span and returning how many were kept. The caller owns clearing whatever is left past that
    /// prefix.
    /// </summary>
    public int SuppressDelivered(Span<SubjectPropertyChange> survivors)
    {
        var kept = 0;
        for (var index = 0; index < survivors.Length; index++)
        {
            ref readonly var survivor = ref survivors[index];
            if (!TryAdmit(in survivor))
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

    /// <summary>
    /// Advances the baseline for a change the processor handles without writing it out, which means an
    /// echo of the source's own value. The source already holds that value at that revision, so leaving
    /// the baseline behind would let a local commit that predates the echo be admitted after it and
    /// overwrite the newer value the source just sent.
    /// </summary>
    public void RecordDelivered(in SubjectPropertyChange change)
    {
        if (change.Revision > 0)
        {
            // Not written out by us: the source already holds this value, having sent or confirmed it.
            TryAdvance(change.Property, change.Revision, writtenOut: false);
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
        return property.TryGetPropertyData(DeliveredRevisionKey, out var value)
               && value is DeliveredRevisionSlots slots
               && slots.TryGetPacked(_source, out var packed)
               && packed < 0;
    }

    /// <summary>
    /// Records the revision as the newest delivered for this property, unless one at least as new is
    /// already recorded. Returns whether it won, which is exactly whether the change should be delivered.
    /// </summary>
    private bool TryAdvance(in PropertyReference property, long revision, bool writtenOut)
    {
        if (_released)
        {
            // Disposed while a flush was still in flight. Recording now would put back a slot nothing
            // will ever remove, so deliver without recording rather than leak.
            return true;
        }

        // Read first: GetOrSetPropertyData takes a value rather than a factory, so calling it
        // unconditionally would allocate a slot holder on every change just to discard it.
        if (!property.TryGetPropertyData(DeliveredRevisionKey, out var value) ||
            value is not DeliveredRevisionSlots slots)
        {
            // Tested rather than cast: property data is a public dictionary, so an unexpected value under
            // this key must not throw here. A throw escapes the merge into the periodic flush loop's own
            // handler, which logs and ends the loop for good, after which the queue grows unbounded.
            if (property.GetOrSetPropertyData(DeliveredRevisionKey, new DeliveredRevisionSlots())
                is not DeliveredRevisionSlots existing)
            {
                return true;
            }

            slots = existing;
        }

        // Sign carries the written-out flag; see DeliveredRevisionSlots for why it is not a shift.
        var admitted = slots.TryAdvance(_source, revision, writtenOut ? -revision : revision, out var slotCreated);
        if (slotCreated)
        {
            _touchedSlots.TryAdd(slots, 0);
        }

        return admitted;
    }

    /// <summary>
    /// Takes this source's slots back out of every property it recorded against, releasing the source.
    /// Called from <see cref="ChangeQueueProcessor.Dispose"/>; without it a connector rebuilt against a
    /// live graph stays reachable from that graph for the lifetime of its subjects.
    /// </summary>
    public void Release()
    {
        _released = true;

        foreach (var slots in _touchedSlots.Keys)
        {
            slots.RemoveSource(_source);
        }

        _touchedSlots.Clear();
    }
}
