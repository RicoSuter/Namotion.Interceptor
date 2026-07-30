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

    // Per property: the slot of its surviving change, plus the revisions of the changes currently
    // supplying the survivor's old and new value. Revisions are only comparable within one subject,
    // which holds here because the key pins the collapse to a single property.
    private readonly Dictionary<PropertyReference, (int Index, long LowestRevision, long HighestRevision)> _propertyIndices
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

        // Deduplicate by Property: keep the lowest revision's old value, use the highest revision's new
        // value. Backward iteration finds last occurrences first, preserving last-occurrence order.
        for (var i = changes.Length - 1; i >= 0; i--)
        {
            var change = changes[i];

            // Single lookup per change: the ref is only read and written before the next add.
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_propertyIndices, change.Property, out var propertyAlreadySeen);
            if (!propertyAlreadySeen)
            {
                entry = (_count, change.Revision, change.Revision);
                _buffer[_count++] = change;
                continue;
            }

            var survivingChange = _buffer[entry.Index];
            if (change.Revision == 0 || entry.LowestRevision == 0 || entry.HighestRevision == 0)
            {
                // A change constructed outside a terminal write carries revision 0, which orders against
                // nothing. Once one takes part, the bounds no longer describe the batch, so the property
                // falls back to arrival position for the rest of the batch: the earlier arrival supplies
                // the old value, the later one the new value.
                _buffer[entry.Index] = change.MergeWithNewer(survivingChange);
                entry = (entry.Index, 0, 0);
            }
            else if (change.Revision <= entry.LowestRevision)
            {
                // Committed no later than the current baseline, so its old value is the batch's baseline.
                // Equal revisions cannot be ordered against each other, so they keep arrival position.
                _buffer[entry.Index] = change.MergeWithNewer(survivingChange);
                entry = (entry.Index, change.Revision, entry.HighestRevision);
            }
            else if (change.Revision > entry.HighestRevision)
            {
                // Committed after the survivor but enqueued before it: its new value is the current state.
                _buffer[entry.Index] = survivingChange.MergeWithNewer(change);
                entry = (entry.Index, entry.LowestRevision, change.Revision);
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
