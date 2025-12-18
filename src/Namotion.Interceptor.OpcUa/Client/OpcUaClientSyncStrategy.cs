using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Sync;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Client-side implementation of OPC UA address space synchronization.
/// Handles creating monitored items for value sync and optionally calling AddNodes/DeleteNodes on the server.
/// </summary>
internal class OpcUaClientSyncStrategy : IOpcUaSyncStrategy
{
    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectClientSource _clientSource;
    private readonly ILogger _logger;
    private readonly Dictionary<NodeId, IInterceptorSubject> _nodeIdToSubject = new();
    private readonly Dictionary<IInterceptorSubject, NodeId> _subjectToNodeId = new();

    private Session? _session;

    public OpcUaClientSyncStrategy(
        OpcUaClientConfiguration configuration,
        OpcUaSubjectClientSource clientSource,
        ILogger logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _clientSource = clientSource ?? throw new ArgumentNullException(nameof(clientSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sets the current OPC UA session. Must be called before any sync operations.
    /// </summary>
    public void SetSession(Session? session)
    {
        _session = session;
    }

    public async Task OnSubjectAttachedAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null || _session is null)
        {
            return;
        }

        _logger.LogDebug(
            "Client: Subject attached - {SubjectType}. Creating monitored items...",
            subject.GetType().Name);

        // TODO Phase 2: Create monitored items for this subject
        // This will be similar to logic in OpcUaSubjectLoader.MonitorValueNode
        // For now, just track the subject

        // Try to create node on server if enabled and supported
        if (_configuration.EnableRemoteNodeManagement)
        {
            await TryCreateRemoteNodeAsync(subject, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task OnSubjectDetachedAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null || _session is null)
        {
            return;
        }

        _logger.LogDebug(
            "Client: Subject detached - {SubjectType}. Removing monitored items...",
            subject.GetType().Name);

        // TODO Phase 2: Remove monitored items for this subject
        // This will integrate with existing SubscriptionManager.RemoveItemsForSubject

        // Try to delete node on server if enabled and supported
        if (_configuration.EnableRemoteNodeManagement)
        {
            await TryDeleteRemoteNodeAsync(subject, cancellationToken).ConfigureAwait(false);
        }

        // Clean up mappings
        if (_subjectToNodeId.TryGetValue(subject, out var nodeId))
        {
            _nodeIdToSubject.Remove(nodeId);
            _subjectToNodeId.Remove(subject);
        }
    }

    public async Task OnRemoteNodeAddedAsync(ReferenceDescription node, NodeId parentNodeId, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        _logger.LogDebug(
            "Client: Remote node added - {NodeId}. Creating local subject...",
            node.NodeId);

        // TODO Phase 4: Implement remote node to local subject creation
        // This will:
        // 1. Find parent subject using parentNodeId
        // 2. Use TypeResolver to infer type or create DynamicSubject
        // 3. Create subject via SubjectFactory
        // 4. Attach to parent collection/property
        // 5. Create monitored items for value sync

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task OnRemoteNodeRemovedAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        _logger.LogDebug(
            "Client: Remote node removed - {NodeId}. Removing local subject...",
            nodeId);

        // Find and detach local subject
        if (_nodeIdToSubject.TryGetValue(nodeId, out var subject))
        {
            // TODO Phase 4: Implement detachment from parent collection/property
            // This will integrate with LifecycleInterceptor

            _nodeIdToSubject.Remove(nodeId);
            _subjectToNodeId.Remove(subject);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<ReferenceDescriptionCollection> BrowseNodeAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return new ReferenceDescriptionCollection();
        }

        const uint nodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

        var (_, _, nodeProperties, _) = await _session.BrowseAsync(
            requestHeader: null,
            view: null,
            [nodeId],
            maxResultsToReturn: 0u,
            BrowseDirection.Forward,
            ReferenceTypeIds.HierarchicalReferences,
            includeSubtypes: true,
            nodeClassMask,
            cancellationToken).ConfigureAwait(false);

        return nodeProperties[0];
    }

    private async Task TryCreateRemoteNodeAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            // TODO Phase 4: Implement AddNodes service call
            // This will call _session.AddNodesAsync with proper node construction

            _logger.LogDebug(
                "Server does not support AddNodes or EnableRemoteNodeManagement is false. " +
                "Local subject '{SubjectType}' will sync values but not structure.",
                subject.GetType().Name);

            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch (ServiceResultException ex) when (
            ex.StatusCode == StatusCodes.BadNotSupported ||
            ex.StatusCode == StatusCodes.BadServiceUnsupported)
        {
            _logger.LogWarning(
                "Server does not support AddNodes service. " +
                "Local subject '{SubjectType}' will sync values but not structure.",
                subject.GetType().Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create remote node for subject {SubjectType}.", subject.GetType().Name);
        }
    }

    private async Task TryDeleteRemoteNodeAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        if (_session is null || !_subjectToNodeId.TryGetValue(subject, out var nodeId))
        {
            return;
        }

        try
        {
            // TODO Phase 4: Implement DeleteNodes service call
            // This will call _session.DeleteNodesAsync

            _logger.LogDebug("Attempted to delete remote node {NodeId}.", nodeId);

            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch (ServiceResultException ex) when (
            ex.StatusCode == StatusCodes.BadNotSupported ||
            ex.StatusCode == StatusCodes.BadServiceUnsupported)
        {
            _logger.LogWarning(
                "Server does not support DeleteNodes service for node {NodeId}.",
                nodeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete remote node {NodeId}.", nodeId);
        }
    }
}
