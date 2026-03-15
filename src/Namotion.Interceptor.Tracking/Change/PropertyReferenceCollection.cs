namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Lock-free, copy-on-write collection for backward property dependencies (UsedByProperties).
/// Concurrency Model:
/// - Reads: Allocation-free via <see cref="Items"/>. Always returns stable snapshot.
/// - Writes: Lock-free CAS (Compare-And-Swap) with automatic retry on contention.
/// Design: Copy-on-write ensures readers never see partial updates.
/// Memory: Allocates on mutation (inherent to copy-on-write). Steady-state is allocation-free.
/// </summary>
public sealed class PropertyReferenceCollection
{
    private PropertyReference[] _items;

    /// <summary>
    /// Shared empty instance for read-only queries when no data exists.
    /// Avoids allocating data objects just to check dependencies.
    /// </summary>
    internal static readonly PropertyReferenceCollection Empty = new();

    internal PropertyReferenceCollection()
    {
        _items = [];
    }

    /// <summary>
    /// Creates a collection pre-populated with a single item.
    /// Avoids a separate Add call (and its CAS array allocation) for the common 0→1 transition.
    /// </summary>
    internal PropertyReferenceCollection(in PropertyReference initialItem)
    {
        _items = [initialItem];
    }

    /// <summary>
    /// Gets the count of dependencies (thread-safe, allocation-free).
    /// </summary>
    public int Count => Volatile.Read(ref _items).Length;

    /// <summary>
    /// Gets a stable snapshot for iteration (thread-safe, allocation-free).
    /// <para>Snapshot won't change even if collection is modified concurrently - copy-on-write semantics.</para>
    /// </summary>
    public ReadOnlySpan<PropertyReference> Items => Volatile.Read(ref _items);

    /// <summary>
    /// Gets the underlying snapshot array (thread-safe). Use when a span cannot be stored
    /// (e.g., across lock boundaries where ref structs are not allowed).
    /// </summary>
    internal PropertyReference[] ItemsArray => Volatile.Read(ref _items);

    /// <summary>
    /// Adds a dependency using lock-free CAS (compare-and-swap).
    /// Thread-safe: Multiple threads can call concurrently. CAS loop retries on contention.
    /// Idempotent: Adding same property multiple times is safe (no duplicates).
    /// </summary>
    /// <returns>True if property was added; false if already exists.</returns>
    internal bool Add(in PropertyReference property)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _items);

            // Idempotency check: Skip if already present
            if (Array.IndexOf(snapshot, property) >= 0)
            {
                return false;
            }

            // Create new array with property appended
            var newArray = new PropertyReference[snapshot.Length + 1];
            Array.Copy(snapshot, newArray, snapshot.Length);
            newArray[^1] = property;

            // Atomic swap: Succeeds if no other thread modified _items
            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, newArray, snapshot), snapshot))
            {
                return true;
            }

            // Another thread won the race - retry with new snapshot
        }
    }

    /// <summary>
    /// Removes a dependency using lock-free CAS (compare-and-swap).
    /// Thread-safe: Multiple threads can call concurrently. CAS loop retries on contention.
    /// </summary>
    /// <returns>True if property was removed; false if not found.</returns>
    internal bool Remove(in PropertyReference property)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _items);

            // Find property to remove
            var idx = Array.IndexOf(snapshot, property);
            if (idx < 0)
            {
                return false; // Not found
            }

            // Create new array without the property
            var newArray = snapshot.Length == 1 ? [] : RemoveAt(snapshot, idx);

            // Atomic swap: Succeeds if no other thread modified _items
            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, newArray, snapshot), snapshot))
            {
                return true;
            }

            // Another thread won the race - retry with new snapshot
        }
    }

    // Helper: Creates new array with item at index removed
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
}
