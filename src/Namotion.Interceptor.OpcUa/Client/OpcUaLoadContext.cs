using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Per-load staged context. All claims and root mutations are queued during
/// discovery and committed via <see cref="Apply"/> on success. If <see cref="Dispose"/>
/// runs before <see cref="Apply"/>, the rollback path detaches the staged subjects that
/// nothing references, so the registry sheds them.
/// <para>
/// Rollback is deliberately not all-or-nothing. A staged subject that was bound to a property
/// during the load is left attached, because the model references it and it is no longer an
/// orphan. Detaching it would evict it from the registry while its parent property still pointed
/// at it, and nothing reconciles those two. So a failed load can leave a partially populated
/// subtree in place, registered and monitored, which the next successful load completes.
/// </para>
/// Unrelated to <c>Namotion.Interceptor.Tracking.Transactions.SubjectTransaction</c>,
/// which captures property-change scopes for the tracking layer.
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
    private readonly Dictionary<PropertyReference, int> _queuedClaimIndices = new(PropertyReference.Comparer);
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
        if (_queuedClaimIndices.TryGetValue(property, out var index))
        {
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
        _queuedClaimIndices[property] = _pendingClaims.Count;
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
    /// the subject can resolve services (registry, interceptors) during discovery. Uses the
    /// immediate parent context rather than the root context, so that in the ordinary tree case
    /// the link matches the one <c>ContextInheritanceHandler</c> removes when the subject's last
    /// property reference goes away.
    /// <para>
    /// The handler does not add a link of its own for a staged subject: its add is gated on the
    /// subject not already being context-attached, and staging makes that false. So this link is
    /// the only one, and whoever removes it must use this same parent context. That holds for a
    /// tree. In a graph-shaped address space the per-load NodeId cache can bind the subject under a
    /// different parent, and the handler's removal is then keyed to that other parent and does
    /// nothing, which <see cref="Dispose"/>'s second pass exists to clean up.
    /// </para>
    /// On rollback we undo this add, but only for subjects that no property references by then.
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
    /// <remarks>
    /// Atomicity is best-effort, not all-or-nothing. The catch-path releases only the
    /// source claims newly established during this Apply call (ownership that predates
    /// this Apply, e.g. from a previous successful load, is retained), but root mutations
    /// (<see cref="QueueOrApplySetValue"/> ops on the root subject) that ran before
    /// the throw cannot be undone because prior values were not captured. After a
    /// mid-Apply throw, expect: (a) ownership consistent (this Apply's new claims
    /// released, pre-existing ownership kept), (b) some root properties may hold subject
    /// references whose <see cref="Dispose"/> rollback then detaches their staged
    /// subjects from the registry. A retry then re-creates fresh subjects and
    /// re-assigns the same root properties.
    /// </remarks>
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
                // A reload re-claims properties this source already owns from a previous
                // successful load (ClaimSource is idempotent-true for the same source).
                // Track only claims newly established by THIS Apply so the rollback below
                // cannot strip pre-existing ownership it never created, which would leave
                // application writes unrouted until the next successful retry.
                var alreadyOwned = property.TryGetSource(out var existingSource) &&
                    ReferenceEquals(existingSource, _source);

                if (!_ownership.ClaimSource(property))
                {
                    _logger.LogError(
                        "Property {Subject}.{Property} already owned by another source. Skipping OPC UA monitoring.",
                        property.Subject.GetType().Name, property.Name);
                    continue;
                }
                if (!alreadyOwned)
                {
                    committedClaims.Add(property);
                }
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

            // A staged subject that acquired a property reference during the load is no longer
            // staged: the model references it, so it is not an orphan to shed. Detaching it here
            // would evict it from the registry while the parent property still points at it, and
            // nothing reconciles those two. ContextInheritanceHandler is otherwise the only caller
            // that removes a fallback context, and it only does so once the last property
            // reference is gone, so this is what keeps that invariant true for the loader too.
            if (staged.GetReferenceCount() > 0)
            {
                continue;
            }

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
        // Second pass, once the detaches above have settled. Removing one staged subject can
        // cascade into another that was skipped a moment ago for still having a reference, and the
        // cascade's own removal is keyed to the parent holding the property reference, which is not
        // necessarily the parent it was staged under: the per-load NodeId cache binds an
        // already-staged subject under a second parent in graph-shaped address spaces. When those
        // differ, the handler's removal is a no-op and the staging link survives, keeping the
        // subject reachable from that parent's context for good. Re-checking here catches it.
        // Removal only, never an add, so this cannot introduce a delegation cycle, and
        // RemoveFallbackContext is a no-op when the link is already gone, so it is order
        // independent and safe to run over entries the first pass already handled.
        for (var i = _stagedSubjects.Count - 1; i >= 0; i--)
        {
            var (staged, parentContext) = _stagedSubjects[i];
            if (staged.GetReferenceCount() != 0)
            {
                continue;
            }

            try
            {
                staged.Context.RemoveFallbackContext(parentContext);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to release the staging context of subject {Subject} during rollback.",
                    staged.GetType().Name);
            }
        }

        _stagedSubjects.Clear();
        _queuedClaimIndices.Clear();
        _pendingClaims.Clear();
        _pendingRootOps.Clear();
    }
}
