using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal sealed class OpcUaLoadContext(
    ISession session,
    uint maximumReferencesPerNode,
    int maxBrowseContinuations,
    ILogger logger,
    CancellationToken cancellationToken)
{
    private readonly Dictionary<NodeId, IReadOnlyList<ReferenceDescription>> _browseCache = new();

    public ISession Session { get; } = session;
    public List<MonitoredItem> MonitoredItems { get; } = new();
    public HashSet<IInterceptorSubject> LoadedSubjects { get; } = new();
    public Dictionary<NodeId, IInterceptorSubject> SubjectsByNodeId { get; } = new();
    public CancellationToken CancellationToken { get; } = cancellationToken;

    public NodeId? ResolveNodeId(ExpandedNodeId expandedNodeId)
    {
        return ExpandedNodeId.ToNodeId(expandedNodeId, Session.NamespaceUris);
    }

    public List<(ReferenceDescription Reference, NodeId NodeId)> DistinctByResolvedNodeId(
        IReadOnlyCollection<ReferenceDescription> references)
    {
        return Session.DistinctByResolvedNodeId(references, logger);
    }

    public async Task<Dictionary<NodeId, IReadOnlyList<ReferenceDescription>>> BrowseAsync(
        IReadOnlyCollection<NodeId> nodeIds)
    {
        var view = new Dictionary<NodeId, IReadOnlyList<ReferenceDescription>>(nodeIds.Count);
        List<NodeId>? missing = null;
        foreach (var nodeId in nodeIds)
        {
            if (view.ContainsKey(nodeId))
            {
                continue;
            }

            if (_browseCache.TryGetValue(nodeId, out var cached))
            {
                view[nodeId] = cached;
            }
            else
            {
                (missing ??= new List<NodeId>(nodeIds.Count)).Add(nodeId);
            }
        }

        if (missing is { Count: > 0 })
        {
            var results = await Session.BrowseNodesAsync(
                missing,
                maximumReferencesPerNode,
                maxBrowseContinuations,
                logger,
                CancellationToken).ConfigureAwait(false);

            foreach (var (nodeId, refs) in results)
            {
                _browseCache[nodeId] = refs;
                view[nodeId] = refs;
            }
        }

        return view;
    }
}
