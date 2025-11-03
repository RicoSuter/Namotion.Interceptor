namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Lock-free, copy-on-write collection for property dependencies.
/// Optimized for frequent reads and infrequent writes with allocation-free reads.
/// </summary>
public sealed class DerivedPropertyDependencies
{
    private PropertyReference[] _items = [];

    /// <summary>
    /// Gets the count of dependencies (thread-safe, allocation-free).
    /// </summary>
    public int Count => Volatile.Read(ref _items).Length;

    /// <summary>
    /// Gets a stable snapshot for iteration (thread-safe, allocation-free).
    /// Returns a span that won't change even if the collection is modified concurrently.
    /// </summary>
    public ReadOnlySpan<PropertyReference> AsSpan()
    {
        return Volatile.Read(ref _items);
    }

    /// <summary>
    /// Checks if dependencies are equal to the provided span (allocation-free, order-insensitive).
    /// </summary>
    internal bool SequenceEqual(ReadOnlySpan<PropertyReference> other)
    {
        var current = AsSpan();
        if (current.Length != other.Length)
            return false;

        // Order-insensitive equality check
        for (int i = 0; i < current.Length; i++)
        {
            bool found = false;
            for (int j = 0; j < other.Length; j++)
            {
                if (current[i] == other[j])
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Adds a dependency using lock-free CAS (copy-on-write).
    /// </summary>
    internal bool Add(in PropertyReference item)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _items);

            // Check if already exists
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] == item)
                    return false; // Already exists
            }

            // Create new array with added item
            var newArr = new PropertyReference[snapshot.Length + 1];
            Array.Copy(snapshot, newArr, snapshot.Length);
            newArr[^1] = item;

            // Atomic swap
            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, newArr, snapshot), snapshot))
                return true;

            // Retry on contention
        }
    }

    /// <summary>
    /// Removes a dependency using lock-free CAS (copy-on-write).
    /// </summary>
    internal bool Remove(in PropertyReference item)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _items);

            // Find item index
            int idx = -1;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] == item)
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0)
                return false; // Not found

            // Create new array without the item
            PropertyReference[] newArr;
            if (snapshot.Length == 1)
            {
                newArr = [];
            }
            else
            {
                newArr = new PropertyReference[snapshot.Length - 1];
                if (idx > 0)
                    Array.Copy(snapshot, 0, newArr, 0, idx);
                if (idx < snapshot.Length - 1)
                    Array.Copy(snapshot, idx + 1, newArr, idx, snapshot.Length - idx - 1);
            }

            // Atomic swap
            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, newArr, snapshot), snapshot))
                return true;

            // Retry on contention
        }
    }

    /// <summary>
    /// Replaces all dependencies with the provided span (thread-safe).
    /// Skips update if dependencies are equal (allocation-free optimization).
    /// </summary>
    internal void ReplaceWith(ReadOnlySpan<PropertyReference> newItems)
    {
        // Fast path: skip if equal
        if (SequenceEqual(newItems))
            return;

        // Create new array
        var newArr = newItems.Length == 0 ? [] : newItems.ToArray();
        Volatile.Write(ref _items, newArr);
    }
}
