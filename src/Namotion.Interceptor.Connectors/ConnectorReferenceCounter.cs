namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Tracks connector-scoped reference counts for subjects with associated data.
/// Thread-safe for concurrent access.
/// </summary>
/// <typeparam name="TData">Connector-specific data type (e.g., NodeState, List&lt;MonitoredItem&gt;).</typeparam>
public class ConnectorReferenceCounter<TData>
{
    private readonly Dictionary<IInterceptorSubject, (int Count, TData Data)> _entries = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Increments reference count. Returns true if this is the first reference.
    /// For first reference, dataFactory is called to create associated data.
    /// </summary>
    /// <param name="subject">The subject to track.</param>
    /// <param name="dataFactory">Factory function to create data on first reference.</param>
    /// <param name="data">The associated data (existing or newly created).</param>
    /// <returns>True if this is the first reference, false otherwise.</returns>
    public bool IncrementAndCheckFirst(IInterceptorSubject subject, Func<TData> dataFactory, out TData data)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(subject, out var entry))
            {
                _entries[subject] = (entry.Count + 1, entry.Data);
                data = entry.Data;
                return false;
            }

            data = dataFactory();
            _entries[subject] = (1, data);
            return true;
        }
    }

    /// <summary>
    /// Decrements reference count. Returns true if this was the last reference.
    /// On last reference, data is returned for cleanup.
    /// </summary>
    /// <param name="subject">The subject to decrement.</param>
    /// <param name="data">The associated data if this was the last reference, default otherwise.</param>
    /// <returns>True if this was the last reference, false otherwise.</returns>
    public bool DecrementAndCheckLast(IInterceptorSubject subject, out TData? data)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(subject, out var entry))
            {
                data = default;
                return false;
            }

            if (entry.Count == 1)
            {
                _entries.Remove(subject);
                data = entry.Data;
                return true;
            }

            _entries[subject] = (entry.Count - 1, entry.Data);
            data = default;
            return false;
        }
    }

    /// <summary>
    /// Gets data for subject if tracked.
    /// </summary>
    /// <param name="subject">The subject to look up.</param>
    /// <param name="data">The associated data if found, default otherwise.</param>
    /// <returns>True if subject is tracked, false otherwise.</returns>
    public bool TryGetData(IInterceptorSubject subject, out TData? data)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(subject, out var entry))
            {
                data = entry.Data;
                return true;
            }
            data = default;
            return false;
        }
    }

    /// <summary>
    /// Clears all entries, returns all data for cleanup.
    /// </summary>
    /// <returns>All tracked data for cleanup.</returns>
    public IEnumerable<TData> Clear()
    {
        lock (_lock)
        {
            var data = _entries.Values.Select(e => e.Data).ToList();
            _entries.Clear();
            return data;
        }
    }

    /// <summary>
    /// Gets all tracked subjects.
    /// </summary>
    /// <returns>All tracked subjects.</returns>
    public IEnumerable<IInterceptorSubject> GetAllSubjects()
    {
        lock (_lock)
        {
            return _entries.Keys.ToList();
        }
    }
}
