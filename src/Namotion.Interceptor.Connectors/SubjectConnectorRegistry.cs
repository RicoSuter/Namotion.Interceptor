namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Unified registry for tracking subjects with external IDs and associated data.
/// Provides atomic registration/unregistration with reference counting.
/// Thread-safe for concurrent access.
/// </summary>
/// <typeparam name="TExternalId">External identifier type (e.g., NodeId).</typeparam>
/// <typeparam name="TData">Associated data type (e.g., List&lt;MonitoredItem&gt;).</typeparam>
public class SubjectConnectorRegistry<TExternalId, TData>
    where TExternalId : notnull
{
    private readonly record struct Entry(TExternalId ExternalId, int RefCount, TData Data);

    private readonly Dictionary<IInterceptorSubject, Entry> _subjects = new();
    private readonly Dictionary<TExternalId, IInterceptorSubject> _idToSubject = new();

    /// <summary>
    /// Lock for synchronization. Protected to allow subclasses to extend atomically.
    /// </summary>
    protected Lock Lock { get; } = new();

    /// <summary>
    /// Registers a subject with an external ID. If already registered, increments ref count.
    /// </summary>
    /// <param name="subject">The subject to register.</param>
    /// <param name="externalId">The external identifier.</param>
    /// <param name="dataFactory">Factory to create data on first reference.</param>
    /// <param name="data">The associated data (existing or newly created).</param>
    /// <param name="isFirstReference">True if this is the first reference.</param>
    /// <returns>True if registered successfully, false if subject is null.</returns>
    public bool Register(
        IInterceptorSubject subject,
        TExternalId externalId,
        Func<TData> dataFactory,
        out TData data,
        out bool isFirstReference)
    {
        lock (Lock)
        {
            return RegisterCore(subject, externalId, dataFactory, out data, out isFirstReference);
        }
    }

    /// <summary>
    /// Core registration logic. Called inside lock.
    /// </summary>
    protected virtual bool RegisterCore(
        IInterceptorSubject subject,
        TExternalId externalId,
        Func<TData> dataFactory,
        out TData data,
        out bool isFirstReference)
    {
        if (subject is null)
        {
            data = default!;
            isFirstReference = false;
            return false;
        }

        if (_subjects.TryGetValue(subject, out var entry))
        {
            _subjects[subject] = entry with { RefCount = entry.RefCount + 1 };
            data = entry.Data;
            isFirstReference = false;
            return true;
        }

        data = dataFactory();
        _subjects[subject] = new Entry(externalId, 1, data);
        _idToSubject[externalId] = subject;
        isFirstReference = true;
        return true;
    }

    /// <summary>
    /// Decrements ref count. Removes entry when last reference is released.
    /// </summary>
    /// <param name="subject">The subject to unregister.</param>
    /// <param name="externalId">The external ID if this was the last reference.</param>
    /// <param name="data">The associated data if this was the last reference.</param>
    /// <param name="wasLastReference">True if this was the last reference.</param>
    /// <returns>True if subject was registered, false if not found.</returns>
    public bool Unregister(
        IInterceptorSubject subject,
        out TExternalId? externalId,
        out TData? data,
        out bool wasLastReference)
    {
        lock (Lock)
        {
            return UnregisterCore(subject, out externalId, out data, out wasLastReference);
        }
    }

    /// <summary>
    /// Core unregistration logic. Called inside lock.
    /// </summary>
    protected virtual bool UnregisterCore(
        IInterceptorSubject subject,
        out TExternalId? externalId,
        out TData? data,
        out bool wasLastReference)
    {
        if (subject is null || !_subjects.TryGetValue(subject, out var entry))
        {
            externalId = default;
            data = default;
            wasLastReference = false;
            return false;
        }

        if (entry.RefCount == 1)
        {
            _subjects.Remove(subject);
            _idToSubject.Remove(entry.ExternalId);
            externalId = entry.ExternalId;
            data = entry.Data;
            wasLastReference = true;
            return true;
        }

        _subjects[subject] = entry with { RefCount = entry.RefCount - 1 };
        externalId = default;
        data = default;
        wasLastReference = false;
        return true;
    }

    /// <summary>
    /// Unregisters by external ID. Used for external deletions.
    /// </summary>
    public bool UnregisterByExternalId(
        TExternalId externalId,
        out IInterceptorSubject? subject,
        out TData? data,
        out bool wasLastReference)
    {
        lock (Lock)
        {
            return UnregisterByExternalIdCore(externalId, out subject, out data, out wasLastReference);
        }
    }

    /// <summary>
    /// Core logic for unregistering by external ID. Called inside lock.
    /// </summary>
    protected virtual bool UnregisterByExternalIdCore(
        TExternalId externalId,
        out IInterceptorSubject? subject,
        out TData? data,
        out bool wasLastReference)
    {
        if (!_idToSubject.TryGetValue(externalId, out subject))
        {
            data = default;
            wasLastReference = false;
            return false;
        }

        var entry = _subjects[subject];
        if (entry.RefCount == 1)
        {
            _subjects.Remove(subject);
            _idToSubject.Remove(externalId);
            data = entry.Data;
            wasLastReference = true;
        }
        else
        {
            _subjects[subject] = entry with { RefCount = entry.RefCount - 1 };
            data = default;
            wasLastReference = false;
        }
        return true;
    }

    /// <summary>
    /// Gets the external ID for a registered subject.
    /// </summary>
    public bool TryGetExternalId(IInterceptorSubject subject, out TExternalId? externalId)
    {
        lock (Lock)
        {
            if (subject is not null && _subjects.TryGetValue(subject, out var entry))
            {
                externalId = entry.ExternalId;
                return true;
            }
            externalId = default;
            return false;
        }
    }

    /// <summary>
    /// Gets the subject for a given external ID.
    /// </summary>
    public bool TryGetSubject(TExternalId externalId, out IInterceptorSubject? subject)
    {
        lock (Lock)
        {
            return _idToSubject.TryGetValue(externalId, out subject);
        }
    }

    /// <summary>
    /// Gets the data for a registered subject.
    /// </summary>
    public bool TryGetData(IInterceptorSubject subject, out TData? data)
    {
        lock (Lock)
        {
            if (subject is not null && _subjects.TryGetValue(subject, out var entry))
            {
                data = entry.Data;
                return true;
            }
            data = default;
            return false;
        }
    }

    /// <summary>
    /// Checks if a subject is registered.
    /// </summary>
    public bool IsRegistered(IInterceptorSubject subject)
    {
        lock (Lock)
        {
            return subject is not null && _subjects.ContainsKey(subject);
        }
    }

    /// <summary>
    /// Updates the external ID for a subject. Used for collection reindexing.
    /// </summary>
    /// <returns>True if updated, false if subject not found or newId already exists for different subject.</returns>
    public bool UpdateExternalId(IInterceptorSubject subject, TExternalId newExternalId)
    {
        lock (Lock)
        {
            return UpdateExternalIdCore(subject, newExternalId);
        }
    }

    /// <summary>
    /// Core logic for updating external ID. Called inside lock.
    /// </summary>
    protected virtual bool UpdateExternalIdCore(IInterceptorSubject subject, TExternalId newExternalId)
    {
        if (subject is null || !_subjects.TryGetValue(subject, out var entry))
        {
            return false;
        }

        // Check for collision (different subject already has this ID)
        if (_idToSubject.TryGetValue(newExternalId, out var existingSubject) &&
            !ReferenceEquals(existingSubject, subject))
        {
            return false;
        }

        // Remove old ID mapping
        _idToSubject.Remove(entry.ExternalId);

        // Update to new ID
        _subjects[subject] = entry with { ExternalId = newExternalId };
        _idToSubject[newExternalId] = subject;
        return true;
    }

    /// <summary>
    /// Modifies the data associated with a subject while holding the lock.
    /// </summary>
    public bool ModifyData(IInterceptorSubject subject, Action<TData> modifier)
    {
        lock (Lock)
        {
            if (subject is null || !_subjects.TryGetValue(subject, out var entry))
            {
                return false;
            }
            modifier(entry.Data);
            return true;
        }
    }

    /// <summary>
    /// Returns a snapshot of all registered subjects.
    /// </summary>
    public List<IInterceptorSubject> GetAllSubjects()
    {
        lock (Lock)
        {
            return [.. _subjects.Keys];
        }
    }

    /// <summary>
    /// Returns a snapshot of all entries.
    /// </summary>
    public List<(IInterceptorSubject Subject, TExternalId ExternalId, TData Data)> GetAllEntries()
    {
        lock (Lock)
        {
            return _subjects.Select(kvp =>
                (kvp.Key, kvp.Value.ExternalId, kvp.Value.Data)).ToList();
        }
    }

    /// <summary>
    /// Removes all entries.
    /// </summary>
    public void Clear()
    {
        lock (Lock)
        {
            ClearCore();
        }
    }

    /// <summary>
    /// Core logic for clearing. Called inside lock.
    /// </summary>
    protected virtual void ClearCore()
    {
        _subjects.Clear();
        _idToSubject.Clear();
    }
}
