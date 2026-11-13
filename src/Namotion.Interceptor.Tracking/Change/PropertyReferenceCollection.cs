using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Thread-safe collection for backward property dependencies (UsedByProperties).
/// Reads via <see cref="Items"/> return a stable snapshot without locking and without allocating, except in
/// a large collection: a read that finds the snapshot cleared by a change takes the lock, and the first such
/// read rebuilds the snapshot while the others wait for that copy.
/// The order of <see cref="Items"/> is unspecified.
/// </summary>
public sealed class PropertyReferenceCollection
{
    private const int MaxSmallCount = 32;
    private const int MaxDemotedCount = 16;

    // Must be a power of two because Sync masks with LockCount - 1.
    private const int LockCount = 64;

    // Collections share a fixed set of locks instead of allocating one each. This is safe because the lock
    // is a leaf: nothing that runs under it takes another lock, so no thread ever holds two of them. Readers
    // also take it to rebuild a large collection's snapshot, which copies the whole collection.
    private static readonly Lock[] Locks = CreateLocks(LockCount);

    // A PropertyReference[] (small mode: copy-on-write, the array is the snapshot) or a LargeStorage.
    // Replaced only under Sync, so writers never race each other on it.
    private object _state;

    /// <summary>
    /// Shared empty instance for read-only queries when no data exists.
    /// Avoids allocating data objects just to check dependencies.
    /// </summary>
    internal static readonly PropertyReferenceCollection Empty = new();

    internal PropertyReferenceCollection()
    {
        _state = Array.Empty<PropertyReference>();
    }

    /// <summary>
    /// Creates a collection pre-populated with a single item.
    /// Avoids a separate Add call (and its array allocation) for the common 0→1 transition.
    /// </summary>
    internal PropertyReferenceCollection(in PropertyReference initialItem)
    {
        _state = new[] { initialItem };
    }

    /// <summary>
    /// Gets the count of dependencies (thread-safe, allocation-free).
    /// </summary>
    public int Count
    {
        get
        {
            var state = Volatile.Read(ref _state);
            return state is PropertyReference[] array ? array.Length : ((LargeStorage)state).Count;
        }
    }

    /// <summary>
    /// Gets a stable snapshot for iteration (thread-safe).
    /// <para>The snapshot does not change when the collection is modified concurrently. The order is unspecified.</para>
    /// </summary>
    public ReadOnlySpan<PropertyReference> Items => ItemsArray;

    /// <summary>
    /// Gets the underlying snapshot array (thread-safe). Use when a span cannot be stored
    /// (e.g., across lock boundaries where ref structs are not allowed).
    /// </summary>
    internal PropertyReference[] ItemsArray
    {
        // Inlined into the write path, so the large-mode branch stays out of line.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _state) as PropertyReference[] ?? GetLargeSnapshot();
    }

    private Lock Sync => Locks[RuntimeHelpers.GetHashCode(this) & (LockCount - 1)];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private PropertyReference[] GetLargeSnapshot()
    {
        // Re-read, since the collection may have been demoted to an array after the caller's read.
        var state = Volatile.Read(ref _state);
        return state as PropertyReference[] ?? ((LargeStorage)state).Snapshot ?? RebuildSnapshot();
    }

    /// <summary>
    /// Returns whether the collection contains the property (thread-safe, allocation-free).
    /// Takes the collection's lock while the collection is large.
    /// </summary>
    internal bool Contains(in PropertyReference property)
    {
        if (Volatile.Read(ref _state) is PropertyReference[] array)
        {
            return Array.IndexOf(array, property) >= 0;
        }

        lock (Sync)
        {
            var state = _state;
            return state is PropertyReference[] current
                ? Array.IndexOf(current, property) >= 0
                : ((LargeStorage)state).Contains(property);
        }
    }

    /// <summary>
    /// Adds a dependency. Thread-safe and idempotent.
    /// </summary>
    /// <returns>True if property was added; false if already exists.</returns>
    /// <remarks>
    /// A successful add ends with a full memory barrier, so the addition is published before the call returns.
    /// Ordering the addition against a later read of another location is the caller's job: dependency registration
    /// does it with its own fence in the version check. An add that returns false publishes nothing and needs no
    /// barrier.
    /// </remarks>
    internal bool Add(in PropertyReference property)
    {
        lock (Sync)
        {
            if (_state is not PropertyReference[] array)
            {
                if (!((LargeStorage)_state).Add(property))
                {
                    return false;
                }
            }
            else if (Array.IndexOf(array, property) >= 0)
            {
                return false;
            }
            else if (array.Length < MaxSmallCount)
            {
                var newArray = new PropertyReference[array.Length + 1];
                Array.Copy(array, newArray, array.Length);
                newArray[^1] = property;
                Volatile.Write(ref _state, newArray);
            }
            else
            {
                Volatile.Write(ref _state, new LargeStorage(array, property));
            }

            // Releasing the lock is not documented to be a full fence.
            Interlocked.MemoryBarrier();
            return true;
        }
    }

    /// <summary>
    /// Removes a dependency. Thread-safe.
    /// </summary>
    /// <returns>True if property was removed; false if not found.</returns>
    internal bool Remove(in PropertyReference property)
    {
        lock (Sync)
        {
            if (_state is PropertyReference[] array)
            {
                var index = Array.IndexOf(array, property);
                if (index < 0)
                {
                    return false;
                }

                Volatile.Write(ref _state, array.Length == 1 ? Array.Empty<PropertyReference>() : RemoveAt(array, index));
                return true;
            }

            var storage = (LargeStorage)_state;
            if (!storage.Remove(property))
            {
                return false;
            }

            if (storage.Count <= MaxDemotedCount)
            {
                Volatile.Write(ref _state, storage.ToArray());
            }

            return true;
        }
    }

    private PropertyReference[] RebuildSnapshot()
    {
        lock (Sync)
        {
            var state = _state;
            return state as PropertyReference[] ?? ((LargeStorage)state).GetOrCreateSnapshot();
        }
    }

    private static PropertyReference[] RemoveAt(PropertyReference[] source, int index)
    {
        var result = new PropertyReference[source.Length - 1];

        if (index > 0)
        {
            Array.Copy(source, 0, result, 0, index);
        }

        if (index < source.Length - 1)
        {
            Array.Copy(source, index + 1, result, index, source.Length - index - 1);
        }

        return result;
    }

    private static Lock[] CreateLocks(int count)
    {
        var locks = new Lock[count];
        for (var index = 0; index < locks.Length; index++)
        {
            locks[index] = new Lock();
        }

        return locks;
    }

    /// <summary>
    /// Storage above <see cref="MaxSmallCount"/>. Members other than <see cref="Count"/> and
    /// <see cref="Snapshot"/> must be called under the owning collection's lock.
    /// </summary>
    private sealed class LargeStorage
    {
        private readonly Dictionary<PropertyReference, int> _indices;

        // Private: readers only ever see copies made by ToArray, so slots may be overwritten in place.
        private PropertyReference[] _buffer;
        private int _count;
        private PropertyReference[]? _snapshot;

        internal LargeStorage(PropertyReference[] items, in PropertyReference item)
        {
            _indices = new Dictionary<PropertyReference, int>(items.Length * 2, PropertyReference.Comparer);
            _buffer = new PropertyReference[items.Length * 2];
            for (var index = 0; index < items.Length; index++)
            {
                _indices.Add(items[index], index);
                _buffer[index] = items[index];
            }

            _count = items.Length;
            Add(item);
        }

        internal int Count => Volatile.Read(ref _count);

        internal PropertyReference[]? Snapshot => Volatile.Read(ref _snapshot);

        internal bool Contains(in PropertyReference property)
        {
            return _indices.ContainsKey(property);
        }

        internal bool Add(in PropertyReference property)
        {
            // Grown before the index changes, so a failed resize leaves the storage unchanged.
            if (_count == _buffer.Length)
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            if (!_indices.TryAdd(property, _count))
            {
                return false;
            }

            // The clear makes readers rebuild, and in large mode it is the store the write generation pairing relies
            // on, so it must stay before the barrier in the outer Add. Only its position before the count update is
            // optional: it keeps a caller that reads Count and then Items from getting an older snapshot.
            Volatile.Write(ref _snapshot, null);
            _buffer[_count] = property;
            Volatile.Write(ref _count, _count + 1);
            return true;
        }

        internal bool Remove(in PropertyReference property)
        {
            if (!_indices.Remove(property, out var index))
            {
                return false;
            }

            // The clear makes readers rebuild, so Items stops listing the removed entry. Only its position before the
            // count update is optional: it keeps a caller that reads Count and then Items from getting an older snapshot.
            Volatile.Write(ref _snapshot, null);

            var lastIndex = _count - 1;
            if (index < lastIndex)
            {
                var moved = _buffer[lastIndex];
                _buffer[index] = moved;
                _indices[moved] = index;
            }

            _buffer[lastIndex] = default;
            Volatile.Write(ref _count, lastIndex);
            return true;
        }

        internal PropertyReference[] GetOrCreateSnapshot()
        {
            var snapshot = _snapshot;
            if (snapshot is null)
            {
                snapshot = ToArray();
                Volatile.Write(ref _snapshot, snapshot);
            }

            return snapshot;
        }

        internal PropertyReference[] ToArray()
        {
            return _buffer.AsSpan(0, _count).ToArray();
        }
    }
}
