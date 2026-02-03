using Namotion.Interceptor.Connectors;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// OPC UA client-specific subject registry that adds recently-deleted tracking.
/// Recently deleted subjects are tracked to prevent periodic resync from re-adding them.
/// </summary>
internal class OpcUaClientSubjectRegistry
    : SubjectConnectorRegistry<NodeId, List<MonitoredItem>>
{
    private readonly Dictionary<NodeId, DateTime> _recentlyDeleted = new();
    private readonly TimeSpan _recentlyDeletedExpiry = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Core unregistration that also marks the subject as recently deleted.
    /// </summary>
    protected override bool UnregisterCore(
        IInterceptorSubject subject,
        out NodeId? externalId,
        out List<MonitoredItem>? data,
        out bool wasLastReference)
    {
        var result = base.UnregisterCore(subject, out externalId, out data, out wasLastReference);

        if (result && wasLastReference && externalId is not null)
        {
            _recentlyDeleted[externalId] = DateTime.UtcNow;
        }

        return result;
    }

    /// <summary>
    /// Checks if a node was recently deleted (within expiry window).
    /// Used to prevent periodic resync from re-adding deleted items.
    /// </summary>
    public bool WasRecentlyDeleted(NodeId nodeId)
    {
        lock (Lock)
        {
            if (!_recentlyDeleted.TryGetValue(nodeId, out var deletedAt))
            {
                return false;
            }

            if (DateTime.UtcNow - deletedAt > _recentlyDeletedExpiry)
            {
                _recentlyDeleted.Remove(nodeId);
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Clears all entries including recently deleted tracking.
    /// </summary>
    protected override void ClearCore()
    {
        base.ClearCore();
        _recentlyDeleted.Clear();
    }
}
