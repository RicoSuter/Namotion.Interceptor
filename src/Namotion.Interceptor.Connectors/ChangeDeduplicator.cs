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

    // Per property: the slot of its surviving change, the revision bounds of the whole batch for that
    // property, and whether any of its changes is unordered (revision 0). Revisions are only comparable
    // within one subject, which holds here because the key pins the collapse to a single property.
    private readonly Dictionary<PropertyReference, (int Index, long LowestRevision, long HighestRevision, bool HasUnorderedChange)> _propertyIndices
        = new(PropertyReference.Comparer);

    // Reusable buffer for deduplicated changes (rented from ArrayPool to avoid allocations on resize)
    private SubjectPropertyChange[] _buffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(BufferMinimumSize);
    private int _count;

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
        // Returning without clearing is safe because Reset() clears the buffer after every batch.
        if (_buffer.Length < changes.Length)
        {
            ArrayPool<SubjectPropertyChange>.Shared.Return(_buffer);
            _buffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(changes.Length);
        }

        // First pass: collect the revision bounds per property, and whether the property has to fall back
        // to arrival position. Merging only starts once these are known for the whole batch, because a
        // merge decided against partial bounds can promote a value that a later change invalidates.
        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes[i];

            // Single lookup per change: the ref is only read and written before the next add.
            ref var bounds = ref CollectionsMarshal.GetValueRefOrAddDefault(_propertyIndices, change.Property, out var propertyAlreadySeen);
            if (!propertyAlreadySeen)
            {
                bounds = (UnplacedIndex, change.Revision, change.Revision, change.Revision == 0);
            }
            else if (change.Revision == 0)
            {
                // A change constructed outside a terminal write carries revision 0, which orders against
                // nothing, so the bounds stop describing the batch and the property falls back entirely
                // to arrival position.
                bounds.HasUnorderedChange = true;
            }
            else if (change.Revision < bounds.LowestRevision)
            {
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
            if (entry.HasUnorderedChange || change.Revision == entry.LowestRevision)
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
    /// the pooled buffer free of stale references.
    /// </summary>
    public void Reset()
    {
        _propertyIndices.Clear();

        // Clear the entire rented array. SubjectPropertyChange holds object references
        // (Source, boxed values) that must be released so they can be collected.
        Array.Clear(_buffer, 0, _buffer.Length);

        if (_buffer.Length >= BufferMaximumSize && _count < _buffer.Length / 4)
        {
            // Shrink buffer if it grew too large (return to pool and rent smaller)
            ArrayPool<SubjectPropertyChange>.Shared.Return(_buffer);
            _buffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(BufferMinimumSize);
        }
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

        Array.Clear(_buffer, 0, _buffer.Length);
        ArrayPool<SubjectPropertyChange>.Shared.Return(_buffer);
        _buffer = null!;
    }
}
