using System.Buffers;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Collapses a flush batch to a single change per property, keeping the batch's baseline old value and
/// its newest new value. Both ends are picked by <see cref="SubjectPropertyChange.Revision"/> (commit
/// order) rather than by arrival position, because a change is enqueued after its commit and outside the
/// subject lock, so concurrent writers to one property can enqueue in the opposite order they committed.
/// Owns the pooled scratch buffers so that a flush allocates nothing per batch.
/// Not thread-safe: the caller must serialize all calls, which <see cref="ChangeQueueProcessor"/> does
/// by holding its flush gate for the whole deduplicate, write and reset cycle.
/// </summary>
internal sealed class ChangeDeduplicator : IDisposable
{
    private const int BufferMinimumSize = 256;
    private const int BufferMaximumSize = 1024;

    // Slot of a property whose surviving change has not been placed into the buffer yet.
    private const int UnplacedIndex = -1;

    // Per property: the slot of its surviving change and the revision bounds of the whole batch for that
    // property. Revisions are only comparable within one subject, which holds here because the key pins
    // the collapse to a single property. A committed write never takes revision 0, so a zero lower bound
    // doubles as the "this property has an unordered change" flag the merge falls back on.
    private readonly Dictionary<PropertyReference, (int Index, long LowestRevision, long HighestRevision)> _propertyIndices
        = new(PropertyReference.Comparer);

    // Reusable buffer for deduplicated changes (rented from ArrayPool to avoid allocations on resize)
    private SubjectPropertyChange[] _buffer = RentClearedBuffer(BufferMinimumSize);
    private int _count;

    /// <summary>
    /// Rents a buffer and clears it once. <see cref="ArrayPool{T}"/> hands out whatever the previous
    /// renter left behind and a <see cref="SubjectPropertyChange"/> carries object references, so this is
    /// what establishes the invariant the flush path relies on: nothing outside <c>[0, _count)</c> holds a
    /// reference, which is why releasing a batch only has to clear that prefix.
    /// </summary>
    private static SubjectPropertyChange[] RentClearedBuffer(int minimumLength)
    {
        var buffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(minimumLength);
        Array.Clear(buffer, 0, buffer.Length);
        return buffer;
    }

    /// <summary>
    /// Collapses the batch to one change per property. The result is ordered by the arrival position of
    /// each property's last occurrence.
    /// </summary>
    /// <param name="changes">The batch to collapse, in arrival order.</param>
    /// <returns>The deduplicated changes. The memory points into the pooled buffer and stays valid until
    /// the next <see cref="Deduplicate"/>, <see cref="Reset"/> or <see cref="Dispose"/> call, so the caller
    /// can await a write handler on it before resetting.</returns>
    public ReadOnlyMemory<SubjectPropertyChange> Deduplicate(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        _propertyIndices.Clear();
        _count = 0;

        if (changes.Length == 0)
        {
            return ReadOnlyMemory<SubjectPropertyChange>.Empty;
        }

        // Pre-size to avoid resizes under bursts
        _propertyIndices.EnsureCapacity(changes.Length);

        // Ensure the buffer is large enough (rent from pool to avoid allocations).
        // Returning without clearing is safe because Reset() released the previous batch, which leaves
        // the whole array clear.
        if (_buffer.Length < changes.Length)
        {
            ArrayPool<SubjectPropertyChange>.Shared.Return(_buffer);
            _buffer = RentClearedBuffer(changes.Length);
        }

        // First pass: collect the revision bounds per property, a lower bound of 0 meaning the property has
        // to fall back to arrival position. Merging only starts once these are known for the whole batch,
        // because a merge decided against partial bounds can promote a value that a later change invalidates.
        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes[i];

            // Single lookup per change: the ref is only read and written before the next add.
            ref var bounds = ref CollectionsMarshal.GetValueRefOrAddDefault(_propertyIndices, change.Property, out var propertyAlreadySeen);
            if (!propertyAlreadySeen)
            {
                bounds = (UnplacedIndex, change.Revision, change.Revision);
            }
            else if (change.Revision < bounds.LowestRevision)
            {
                // A change constructed outside a terminal write carries revision 0, which orders against
                // nothing. Revisions start at 1, so such a change drives the lower bound to 0 through this
                // plain minimum, and the merge reads that as the signal to fall back to arrival position.
                bounds.LowestRevision = change.Revision;
            }
            else if (change.Revision > bounds.HighestRevision)
            {
                bounds.HighestRevision = change.Revision;
            }
        }

        // Second pass: keep the lowest revision's old value and the highest revision's new value, or, for
        // a property that has an unordered change, the first arrival's old value and the last arrival's
        // new value. Backward iteration finds last occurrences first, preserving last-occurrence order.
        for (var i = changes.Length - 1; i >= 0; i--)
        {
            var change = changes[i];

            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_propertyIndices, change.Property);
            if (entry.Index == UnplacedIndex)
            {
                // The property's last arrival, which seeds the survivor with its old and new value.
                entry.Index = _count;
                _buffer[_count++] = change;
                continue;
            }

            var survivingChange = _buffer[entry.Index];
            if (entry.LowestRevision == 0 || change.Revision == entry.LowestRevision)
            {
                // The earlier arrival, respectively the batch's baseline commit, supplies the old value.
                // Under the fallback every earlier arrival overwrites it, so the first one wins.
                _buffer[entry.Index] = change.MergeWithNewer(survivingChange);
            }
            else if (change.Revision == entry.HighestRevision)
            {
                // Committed after the last arrival but enqueued before it: its new value is the current
                // state. Two changes of one property cannot share a nonzero revision, because every
                // committed write takes a strictly incremented revision under the subject's lock.
                _buffer[entry.Index] = survivingChange.MergeWithNewer(change);
            }

            // Any other revision lies inside the bounds, so it is neither the baseline nor the newest
            // state and contributes nothing to the survivor.
        }

        // Reverse to restore chronological order of last occurrences
        if (_count > 1)
        {
            Array.Reverse(_buffer, 0, _count);
        }

        return new ReadOnlyMemory<SubjectPropertyChange>(_buffer, 0, _count);
    }

    /// <summary>
    /// Releases the batch state after the write handler has consumed the result, invalidating the memory
    /// returned by <see cref="Deduplicate"/>. Must be called after every batch, because it is what keeps
    /// the pooled buffer free of stale references. A no-op once <see cref="Dispose"/> has run.
    /// </summary>
    public void Reset()
    {
        // Idempotent like Dispose. Both are called from the same finally block in ChangeQueueProcessor,
        // picked by a disposal flag read outside the buffer's own lifetime, so a released buffer must not
        // turn into an ArgumentNullException raised from a finally block, which would replace whatever the
        // flush was propagating.
        if (_buffer is null)
        {
            return;
        }

        _propertyIndices.Clear();

        // Only the prefix Deduplicate filled can hold object references (subjects, boxed values): every
        // buffer is cleared once when it is rented and every batch is released here, so the rest of the
        // array is already clear. Clearing the whole rental instead would make a small batch pay for the
        // 256 slot minimum.
        Array.Clear(_buffer, 0, _count);

        if (_buffer.Length >= BufferMaximumSize && _count < _buffer.Length / 4)
        {
            // Shrink buffer if it grew too large (return to pool and rent smaller)
            ArrayPool<SubjectPropertyChange>.Shared.Return(_buffer);
            _buffer = RentClearedBuffer(BufferMinimumSize);
        }

        // No batch is held any more, and a shrink can leave a buffer shorter than the old count.
        _count = 0;
    }

    /// <summary>
    /// Clears and returns the pooled buffer, invalidating the memory returned by
    /// <see cref="Deduplicate"/>. Idempotent, but not safe to call while a batch is in flight.
    /// </summary>
    public void Dispose()
    {
        if (_buffer is null)
        {
            return;
        }

        _propertyIndices.Clear();

        // The same prefix as Reset: this runs either after a Reset, where the count is zero, or instead
        // of it when the processor was disposed mid flush, where the count still bounds what that flush
        // wrote. Everything past it was cleared when the buffer was rented.
        Array.Clear(_buffer, 0, _count);
        ArrayPool<SubjectPropertyChange>.Shared.Return(_buffer);
        _buffer = null!;
        _count = 0;
    }
}
