using Namotion.Interceptor.Connectors;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Graph;

/// <summary>
/// Tracks subjects with their NodeIds using reference counting.
/// Shared between client and server.
/// </summary>
internal class SubjectTracker
{
    // TODO: Merge SubjectTracker with ConnectorReferenceCounter into ConnectorReferenceManager or similar name? 
    
    private readonly ConnectorReferenceCounter<NodeId> _refCounter = new();
    private readonly Dictionary<NodeId, IInterceptorSubject> _nodeIdToSubject = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Tracks a subject with its associated NodeId.
    /// </summary>
    /// <param name="subject">The subject to track.</param>
    /// <param name="nodeId">The OPC UA NodeId associated with this subject.</param>
    /// <returns>True if this is the first reference (subject was newly added), false if already tracked.</returns>
    public bool TrackSubject(IInterceptorSubject subject, NodeId nodeId)
    {
        lock (_lock)
        {
            var isFirst = _refCounter.IncrementAndCheckFirst(subject, () => nodeId, out _);
            if (isFirst)
            {
                _nodeIdToSubject[nodeId] = subject;
            }
            return isFirst;
        }
    }

    /// <summary>
    /// Removes a reference to a subject. When the last reference is removed, the subject is untracked.
    /// </summary>
    /// <param name="subject">The subject to untrack.</param>
    /// <param name="nodeId">The NodeId that was associated with the subject if it was the last reference, null otherwise.</param>
    /// <returns>True if this was the last reference and the subject was removed, false if still has references.</returns>
    public bool UntrackSubject(IInterceptorSubject subject, out NodeId? nodeId)
    {
        lock (_lock)
        {
            var isLast = _refCounter.DecrementAndCheckLast(subject, out nodeId);
            if (isLast && nodeId is not null)
            {
                _nodeIdToSubject.Remove(nodeId);
            }
            return isLast;
        }
    }

    /// <summary>
    /// Tries to get the NodeId for a tracked subject.
    /// </summary>
    /// <param name="subject">The subject to look up.</param>
    /// <param name="nodeId">The NodeId if found, null otherwise.</param>
    /// <returns>True if the subject is tracked, false otherwise.</returns>
    public bool TryGetNodeId(IInterceptorSubject subject, out NodeId? nodeId)
    {
        lock (_lock)
        {
            return _refCounter.TryGetData(subject, out nodeId);
        }
    }

    /// <summary>
    /// Tries to get the subject for a given NodeId.
    /// </summary>
    /// <param name="nodeId">The NodeId to look up.</param>
    /// <param name="subject">The subject if found, null otherwise.</param>
    /// <returns>True if a subject exists for this NodeId, false otherwise.</returns>
    public bool TryGetSubject(NodeId nodeId, out IInterceptorSubject? subject)
    {
        lock (_lock)
        {
            return _nodeIdToSubject.TryGetValue(nodeId, out subject);
        }
    }

    /// <summary>
    /// Gets all currently tracked subjects.
    /// </summary>
    /// <returns>A list of all tracked subjects (snapshot at the time of call).</returns>
    public IEnumerable<IInterceptorSubject> GetAllSubjects()
    {
        lock (_lock)
        {
            return _nodeIdToSubject.Values.ToList();
        }
    }

    /// <summary>
    /// Clears all tracked subjects and their NodeId mappings.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _refCounter.Clear();
            _nodeIdToSubject.Clear();
        }
    }
}
