namespace Namotion.Interceptor.Connectors;

/// <summary>
/// The per-property delivery state <see cref="DeliveredRevisionFilter"/> keeps on the subject: one packed
/// slot per source that delivers this property. Lives in the subject's property data, so it is collected
/// with the subject and needs no eviction.
///
/// A slot packs the newest delivered revision and whether this processor has ever written the property
/// out, into a single long, so it advances with one compare-and-exchange and readers never see the two
/// halves disagree. The flag is the sign: a committed revision is always positive, so negating it carries
/// the bit without shifting, which keeps the full range usable. Shifting would overflow near long.MaxValue
/// and silently invert the comparison. The bit is sticky (see <see cref="TryAdvance"/>).
///
/// The slot array is copy-on-write. Adding a source happens once per property per connector; after that
/// every operation is a reference scan over one or two entries plus an atomic on the slot.
/// </summary>
internal sealed class DeliveredRevisionSlots
{
    // Reads use Volatile rather than Interlocked: Interlocked.Read compiles to a lock cmpxchg on x64,
    // so it dirties the cache line even when only reading. A volatile read of a long is a plain atomic
    // load on 64 bit and remains atomic on 32 bit.
    private sealed class Slot(object source)
    {
        internal readonly object Source = source;
        internal long Packed;
    }

    private Slot[] _slots = [];

    /// <summary>
    /// Reads this source's packed state, or false when it has never delivered this property.
    /// </summary>
    public bool TryGetPacked(object source, out long packed)
    {
        var snapshot = Volatile.Read(ref _slots);
        var index = IndexOf(snapshot, source);
        if (index >= 0)
        {
            packed = Volatile.Read(ref snapshot[index].Packed);
            return true;
        }

        packed = 0;
        return false;
    }

    /// <summary>
    /// Records the revision as the newest delivered by this source, unless one at least as new is already
    /// recorded. Returns whether it won. The written-out bit is sticky: once this source has written the
    /// property to the wire, every later record keeps the bit, because no client-side event can prove
    /// that an earlier write of ours did not land on the source after a transaction's direct write.
    /// </summary>
    public bool TryAdvance(object source, long revision, bool writtenOut, out bool slotCreated)
    {
        var slot = GetOrAddSlot(source, out slotCreated);
        while (true)
        {
            var current = Volatile.Read(ref slot.Packed);
            if (revision <= (current < 0 ? -current : current))
            {
                // Already superseded, by an earlier call or by a concurrent one that got there first.
                // A negative revision, which only the public change factory can produce and which
                // ChangeMerger also treats as outside the contract, never clears this and is dropped.
                return false;
            }

            // Sign carries the sticky written-out bit; recomputed per iteration because a lost race
            // may have set it in the meantime and the OR must see the fresh value.
            var desired = writtenOut || current < 0 ? -revision : revision;
            if (Interlocked.CompareExchange(ref slot.Packed, desired, current) == current)
            {
                return true;
            }

            // Lost the race; re-read and re-test rather than overwrite, so the revision only moves
            // forward and the winner is always the newest commit.
        }
    }

    /// <summary>
    /// Drops this source's slot, releasing the source itself. Called when a processor is disposed: the
    /// subject holds this object, so a slot left behind would keep a dead connector reachable from a
    /// live graph for as long as the subject exists.
    /// </summary>
    public void RemoveSource(object source)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _slots);
            var index = IndexOf(snapshot, source);
            if (index < 0)
            {
                return;
            }

            var updated = new Slot[snapshot.Length - 1];
            Array.Copy(snapshot, 0, updated, 0, index);
            Array.Copy(snapshot, index + 1, updated, index, snapshot.Length - index - 1);

            if (ReferenceEquals(Interlocked.CompareExchange(ref _slots, updated, snapshot), snapshot))
            {
                return;
            }
        }
    }

    private static int IndexOf(Slot[] slots, object source)
    {
        for (var index = 0; index < slots.Length; index++)
        {
            if (ReferenceEquals(slots[index].Source, source))
            {
                return index;
            }
        }

        return -1;
    }

    private Slot GetOrAddSlot(object source, out bool slotCreated)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _slots);
            var existing = IndexOf(snapshot, source);
            if (existing >= 0)
            {
                slotCreated = false;
                return snapshot[existing];
            }

            var added = new Slot(source);
            var updated = new Slot[snapshot.Length + 1];
            Array.Copy(snapshot, updated, snapshot.Length);
            updated[snapshot.Length] = added;

            if (ReferenceEquals(Interlocked.CompareExchange(ref _slots, updated, snapshot), snapshot))
            {
                slotCreated = true;
                return added;
            }
        }
    }
}
