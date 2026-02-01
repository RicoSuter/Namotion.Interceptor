namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Bidirectional mapping between subjects and external identifiers with reference counting.
/// Used by connectors to track which subjects are connected and their external IDs.
/// Thread-safe for concurrent access.
/// </summary>
/// <typeparam name="TExternalId">The type of external identifier (e.g., NodeId for OPC UA).</typeparam>
public class ConnectorSubjectMapping<TExternalId> where TExternalId : notnull
{
    private readonly Dictionary<IInterceptorSubject, (TExternalId Id, int RefCount)> _subjectToId = new();
    private readonly Dictionary<TExternalId, IInterceptorSubject> _idToSubject = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Registers a subject with its external identifier.
    /// Returns true if this is the first reference (subject newly registered).
    /// </summary>
    /// <param name="subject">The subject to register.</param>
    /// <param name="externalId">The external identifier for the subject.</param>
    /// <returns>True if this is the first reference, false otherwise.</returns>
    public bool Register(IInterceptorSubject subject, TExternalId externalId)
    {
        lock (_lock)
        {
            if (_subjectToId.TryGetValue(subject, out var entry))
            {
                _subjectToId[subject] = (entry.Id, entry.RefCount + 1);
                return false;
            }

            _subjectToId[subject] = (externalId, 1);
            _idToSubject[externalId] = subject;
            return true;
        }
    }

    /// <summary>
    /// Unregisters a reference to a subject.
    /// Returns true if this was the last reference (subject fully unregistered).
    /// </summary>
    /// <param name="subject">The subject to unregister.</param>
    /// <param name="externalId">The external ID if this was the last reference, null otherwise.</param>
    /// <returns>True if this was the last reference, false otherwise.</returns>
    public bool Unregister(IInterceptorSubject subject, out TExternalId? externalId)
    {
        lock (_lock)
        {
            if (!_subjectToId.TryGetValue(subject, out var entry))
            {
                externalId = default;
                return false;
            }

            if (entry.RefCount == 1)
            {
                _subjectToId.Remove(subject);
                _idToSubject.Remove(entry.Id);
                externalId = entry.Id;
                return true;
            }

            _subjectToId[subject] = (entry.Id, entry.RefCount - 1);
            externalId = default;
            return false;
        }
    }

    /// <summary>
    /// Gets the external identifier for a registered subject.
    /// </summary>
    /// <param name="subject">The subject to look up.</param>
    /// <param name="externalId">The external ID if found, null otherwise.</param>
    /// <returns>True if the subject is registered, false otherwise.</returns>
    public bool TryGetExternalId(IInterceptorSubject subject, out TExternalId? externalId)
    {
        lock (_lock)
        {
            if (_subjectToId.TryGetValue(subject, out var entry))
            {
                externalId = entry.Id;
                return true;
            }
            externalId = default;
            return false;
        }
    }

    /// <summary>
    /// Gets the subject for a given external identifier.
    /// </summary>
    /// <param name="externalId">The external ID to look up.</param>
    /// <param name="subject">The subject if found, null otherwise.</param>
    /// <returns>True if the external ID is registered, false otherwise.</returns>
    public bool TryGetSubject(TExternalId externalId, out IInterceptorSubject? subject)
    {
        lock (_lock)
        {
            return _idToSubject.TryGetValue(externalId, out subject);
        }
    }

    /// <summary>
    /// Gets all currently registered subjects.
    /// </summary>
    /// <returns>A list of all registered subjects.</returns>
    public IEnumerable<IInterceptorSubject> GetAllSubjects()
    {
        lock (_lock)
        {
            return _subjectToId.Keys.ToList();
        }
    }

    /// <summary>
    /// Clears all registered subjects and their mappings.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _subjectToId.Clear();
            _idToSubject.Clear();
        }
    }
}
