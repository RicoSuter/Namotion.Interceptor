using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Per-load transactional context. All claims and root mutations are queued during
/// discovery and committed via <see cref="Apply"/> on success. If <see cref="Dispose"/>
/// runs before <see cref="Apply"/>, the rollback path detaches staged subjects from
/// the root context so the registry sheds them and the next load starts on a clean slate.
/// </summary>
internal sealed class OpcUaLoadContext : IDisposable
{
    private readonly IInterceptorSubject _rootSubject;
    private readonly SourceOwnershipManager _ownership;
    private readonly OpcUaSubjectClientSource _source;
    private readonly uint _maxReferencesPerNode;
    private readonly int _maxBrowseContinuations;
    private readonly ILogger _logger;
    private readonly Dictionary<NodeId, IReadOnlyList<ReferenceDescription>> _browseCache = new();
    private readonly List<(PropertyReference Property, NodeId NodeId, MonitoredItem MonitoredItem)> _pendingClaims = new();
    private readonly HashSet<PropertyReference> _queuedClaimProperties = new(PropertyReference.Comparer);
    private readonly List<Action> _pendingRootOps = new();
    private readonly List<(IInterceptorSubject Subject, IInterceptorSubjectContext ParentContext)> _stagedSubjects = new();
    private bool _committed;

    public OpcUaLoadContext(
        ISession session,
        IInterceptorSubject rootSubject,
        SourceOwnershipManager ownership,
        OpcUaSubjectClientSource source,
        uint maxReferencesPerNode,
        int maxBrowseContinuations,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Session = session;
        _rootSubject = rootSubject;
        _ownership = ownership;
        _source = source;
        _maxReferencesPerNode = maxReferencesPerNode;
        _maxBrowseContinuations = maxBrowseContinuations;
        _logger = logger;
        CancellationToken = cancellationToken;
    }

    public ISession Session { get; }
    public IInterceptorSubject RootSubject => _rootSubject;
    public List<MonitoredItem> MonitoredItems { get; } = new();
    public HashSet<IInterceptorSubject> LoadedSubjects { get; } = new();
    public Dictionary<NodeId, IInterceptorSubject> SubjectsByNodeId { get; } = new();
    public CancellationToken CancellationToken { get; }

    public NodeId? ResolveNodeId(ExpandedNodeId expandedNodeId)
    {
        return ExpandedNodeId.ToNodeId(expandedNodeId, Session.NamespaceUris);
    }

    public List<(ReferenceDescription Reference, NodeId NodeId)> DistinctByResolvedNodeId(
        IReadOnlyCollection<ReferenceDescription> references)
    {
        return Session.DistinctByResolvedNodeId(references, _logger);
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
                _maxReferencesPerNode,
                _maxBrowseContinuations,
                _logger,
                CancellationToken).ConfigureAwait(false);

            foreach (var (nodeId, refs) in results)
            {
                _browseCache[nodeId] = refs;
                view[nodeId] = refs;
            }
        }

        return view;
    }

    /// <summary>
    /// Queues a source-ownership claim and its associated monitored item. Both are
    /// applied atomically during <see cref="Apply"/>: the monitored item is only added
    /// to <see cref="MonitoredItems"/> on successful claim, so a property that's owned
    /// by a different source by the time Apply runs never gets monitored. Duplicate
    /// claims for the same property (graph-shaped address spaces where the same
    /// PropertyReference is reached via multiple paths) are deduped so a property
    /// never gets monitored twice; when the duplicate carries a different NodeId,
    /// the smaller NodeId wins so the outcome is reproducible across loads regardless
    /// of browse order. On rollback, the entry is discarded.
    /// </summary>
    public void QueueClaim(PropertyReference property, NodeId nodeId, MonitoredItem monitoredItem)
    {
        if (!_queuedClaimProperties.Add(property))
        {
            var index = _pendingClaims.FindIndex(c => PropertyReference.Comparer.Equals(c.Property, property));
            var existing = _pendingClaims[index];
            if (existing.NodeId != nodeId)
            {
                _logger.LogWarning(
                    "Duplicate claim for {Subject}.{Property} with different NodeId (existing: {ExistingNodeId}, new: {NewNodeId}). Keeping the smaller NodeId for deterministic outcome.",
                    property.Subject.GetType().Name, property.Name, existing.NodeId, nodeId);
                if (nodeId.CompareTo(existing.NodeId) < 0)
                {
                    _pendingClaims[index] = (property, nodeId, monitoredItem);
                }
            }
            return;
        }
        _pendingClaims.Add((property, nodeId, monitoredItem));
    }

    /// <summary>
    /// Queues a <c>SetValueFromSource</c> if the property is owned by the root subject,
    /// otherwise applies it live. Centralized so loader call sites don't have to mention
    /// the root-deferral rule directly.
    /// </summary>
    public void QueueOrApplySetValue(object source, RegisteredSubjectProperty property, object? value)
    {
        if (ReferenceEquals(property.Subject, _rootSubject))
        {
            _pendingRootOps.Add(() => property.SetValueFromSource(source, null, null, value));
        }
        else
        {
            property.SetValueFromSource(source, null, null, value);
        }
    }

    /// <summary>
    /// Registers a newly constructed subject and adds the parent context as fallback so
    /// the subject can resolve services (registry, interceptors) during discovery. Uses
    /// the immediate parent context rather than the root context so the link is symmetric
    /// with <c>ContextInheritanceHandler</c>: when the subject is later attached normally
    /// via <c>SetValueFromSource</c>, the handler adds the same parent fallback (deduped),
    /// and when the subject is eventually detached the handler removes it cleanly. On
    /// rollback, we undo our manual add against the same parent context.
    /// </summary>
    public void RegisterStagedSubject(IInterceptorSubject subject, IInterceptorSubjectContext parentContext)
    {
        // Record the rollback entry BEFORE the side effect. If AddFallbackContext throws
        // or _stagedSubjects.Add throws after the side effect, the link could leak;
        // recording first ensures rollback always sees what was actually added.
        _stagedSubjects.Add((subject, parentContext));
        try
        {
            subject.Context.AddFallbackContext(parentContext);
        }
        catch
        {
            _stagedSubjects.RemoveAt(_stagedSubjects.Count - 1);
            throw;
        }
    }

    /// <summary>
    /// Commits the load: claims source ownership for every queued property, then
    /// runs the queued root mutations. Claims run first so an observer that sees
    /// a new root child appear finds all of the child's leaves already source-owned.
    /// </summary>
    public void Apply()
    {
        // Best-effort atomicity: if an op throws mid-Apply, release any source claims
        // we already committed so the next retry isn't blocked by stale ownership.
        // Root mutations that already ran can't be undone (we don't know prior values),
        // but releasing claims at least keeps source ownership consistent.
        var committedClaims = new List<PropertyReference>(_pendingClaims.Count);
        try
        {
            foreach (var (property, nodeId, monitoredItem) in _pendingClaims)
            {
                if (!_ownership.ClaimSource(property))
                {
                    _logger.LogError(
                        "Property {Subject}.{Property} already owned by another source. Skipping OPC UA monitoring.",
                        property.Subject.GetType().Name, property.Name);
                    continue;
                }
                committedClaims.Add(property);
                property.SetPropertyData(_source.OpcUaNodeIdKey, nodeId);
                MonitoredItems.Add(monitoredItem);
            }

            foreach (var op in _pendingRootOps)
            {
                op();
            }

            _committed = true;
        }
        catch
        {
            foreach (var property in committedClaims)
            {
                try { _ownership.ReleaseSource(property); }
                catch (Exception releaseException)
                {
                    _logger.LogWarning(releaseException,
                        "Failed to release source ownership for {Subject}.{Property} during Apply rollback.",
                        property.Subject.GetType().Name, property.Name);
                }
            }
            MonitoredItems.Clear();
            throw;
        }
    }

    public void Dispose()
    {
        if (_committed) return;

        // Rollback in reverse order. Nested staged subjects (e.g. ChildB under ParentA)
        // reach the lifecycle interceptor through their parent context's fallback chain.
        // If we removed ParentA's fallback to root first, ChildB.Context.RemoveFallbackContext
        // would no longer find any ILifecycleInterceptor through parentA.Context and the
        // detach (and registry removal) would be skipped. Removing deepest-first preserves
        // the chain until each staged subject has detached.
        //
        // Each iteration is guarded so one failed detach doesn't strand the rest (which
        // would leak staged subjects into the registry) or mask the load's original
        // exception (Dispose runs via using, and a throw here would supersede).
        for (var i = _stagedSubjects.Count - 1; i >= 0; i--)
        {
            var (staged, parentContext) = _stagedSubjects[i];
            try
            {
                staged.Context.RemoveFallbackContext(parentContext);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to detach staged subject {Subject} from parent context during rollback.",
                    staged.GetType().Name);
            }
        }
        _stagedSubjects.Clear();
        _queuedClaimProperties.Clear();
        _pendingClaims.Clear();
        _pendingRootOps.Clear();
    }
}
