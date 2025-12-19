using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Sync;

/// <summary>
/// Coordinates bidirectional synchronization between local subject graph and OPC UA address space.
/// Handles both local changes (attach/detach) and remote changes (ModelChangeEvents).
/// Thread-safe: Uses semaphore to serialize concurrent sync operations.
/// </summary>
public class OpcUaAddressSpaceSync : IDisposable
{
    private readonly IOpcUaSyncStrategy _strategy;
    private readonly OpcUaConfigurationBase _configuration;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly HashSet<IInterceptorSubject> _processedSubjects = new();
    private readonly HashSet<NodeId> _knownRemoteNodeIds = new();
    private readonly CancellationTokenSource _disposalCts = new();

    private LifecycleInterceptor? _lifecycleInterceptor;
    private Timer? _periodicResyncTimer;
    private NodeId? _rootNodeId;
    private bool _disposed;

    public OpcUaAddressSpaceSync(
        IOpcUaSyncStrategy strategy,
        OpcUaConfigurationBase configuration,
        ILogger logger)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Initializes synchronization by subscribing to lifecycle events and starting periodic resync if enabled.
    /// </summary>
    /// <param name="rootSubject">The root subject to sync.</param>
    /// <param name="rootNodeId">The root OPC UA node ID for periodic resync (optional).</param>
    public void Initialize(IInterceptorSubject rootSubject, NodeId? rootNodeId = null)
    {
        _rootNodeId = rootNodeId;

        if (!_configuration.EnableStructureSynchronization)
        {
            return;
        }

        _lifecycleInterceptor = rootSubject.Context.TryGetLifecycleInterceptor();
        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectAttached += OnSubjectAttached;
            _lifecycleInterceptor.SubjectDetached += OnSubjectDetached;
            _logger.LogInformation("OPC UA address space sync initialized with lifecycle tracking.");
        }
        else
        {
            _logger.LogWarning(
                "OPC UA address space sync enabled but no LifecycleInterceptor found. " +
                "Local changes will not be synchronized.");
        }

        if (_configuration.EnablePeriodicResynchronization)
        {
            _periodicResyncTimer = new Timer(
                _ => _ = PeriodicResyncAsync(),
                null,
                _configuration.PeriodicResynchronizationInterval,
                _configuration.PeriodicResynchronizationInterval);

            _logger.LogInformation(
                "Periodic address space resync enabled with interval: {Interval}",
                _configuration.PeriodicResynchronizationInterval);
        }
    }

    /// <summary>
    /// Sets the root node ID for periodic resynchronization.
    /// This can be called after Initialize if the root node ID is not known at initialization time.
    /// </summary>
    public void SetRootNodeId(NodeId? rootNodeId)
    {
        _rootNodeId = rootNodeId;
    }

    /// <summary>
    /// Handles local subject attached events.
    /// Delegates to strategy to create corresponding OPC UA structures.
    /// </summary>
    private void OnSubjectAttached(SubjectLifecycleChange change)
    {
        // Check if disposal is in progress
        if (_disposalCts.IsCancellationRequested)
        {
            return;
        }

        // Fire and forget - don't block lifecycle event, but capture exceptions in the async method
        _ = Task.Run(() => OnSubjectAttachedAsync(change, _disposalCts.Token));
    }

    private async Task OnSubjectAttachedAsync(SubjectLifecycleChange change, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_processedSubjects.Contains(change.Subject))
            {
                return; // Already processed
            }

            _processedSubjects.Add(change.Subject);

            _logger.LogDebug(
                "Local subject attached: {SubjectType}. Syncing to OPC UA...",
                change.Subject.GetType().Name);

            await _strategy.OnSubjectAttachedAsync(change, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Subject sync completed for {SubjectType}.", change.Subject.GetType().Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposal in progress - exit silently
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to sync attached subject {SubjectType} to OPC UA.",
                change.Subject.GetType().Name);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Handles local subject detached events.
    /// Delegates to strategy to remove corresponding OPC UA structures.
    /// </summary>
    private void OnSubjectDetached(SubjectLifecycleChange change)
    {
        // Check if disposal is in progress
        if (_disposalCts.IsCancellationRequested)
        {
            return;
        }

        // Fire and forget - don't block lifecycle event, but capture exceptions in the async method
        _ = Task.Run(() => OnSubjectDetachedAsync(change, _disposalCts.Token));
    }

    private async Task OnSubjectDetachedAsync(SubjectLifecycleChange change, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _processedSubjects.Remove(change.Subject);

            _logger.LogDebug(
                "Local subject detached: {SubjectType}. Syncing to OPC UA...",
                change.Subject.GetType().Name);

            await _strategy.OnSubjectDetachedAsync(change, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Subject detach sync completed for {SubjectType}.", change.Subject.GetType().Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposal in progress - exit silently
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to sync detached subject {SubjectType} to OPC UA.",
                change.Subject.GetType().Name);
        }
        finally
        {
            // Ensure mapping is cleaned up even if exception occurred (memory leak prevention)
            _strategy.EnsureUnregistered(change.Subject);
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Handles remote OPC UA node added events (from ModelChangeEvents or periodic browse).
    /// Delegates to strategy to create corresponding local subject.
    /// </summary>
    public async Task OnRemoteNodeAddedAsync(ReferenceDescription node, NodeId parentNodeId, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogDebug(
                "Remote node added: {NodeId} under {ParentNodeId}. Syncing to local...",
                node.NodeId,
                parentNodeId);

            // Check if we should add this dynamic property
            if (_configuration.ShouldAddDynamicProperty is not null)
            {
                var shouldAdd = await _configuration.ShouldAddDynamicProperty(node, cancellationToken).ConfigureAwait(false);
                if (!shouldAdd)
                {
                    _logger.LogDebug("Skipping remote node {NodeId} per ShouldAddDynamicProperty predicate.", node.NodeId);
                    return;
                }
            }

            await _strategy.OnRemoteNodeAddedAsync(node, parentNodeId, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Remote node sync completed for {NodeId}.", node.NodeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync remote node {NodeId} to local subject.", node.NodeId);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Handles remote OPC UA node removed events (from ModelChangeEvents or periodic browse).
    /// Delegates to strategy to detach corresponding local subject.
    /// </summary>
    public async Task OnRemoteNodeRemovedAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogDebug("Remote node removed: {NodeId}. Syncing to local...", nodeId);

            await _strategy.OnRemoteNodeRemovedAsync(nodeId, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Remote node removal sync completed for {NodeId}.", nodeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync remote node removal {NodeId} to local subject.", nodeId);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Performs periodic full resync by comparing local and remote address spaces.
    /// Only runs if EnablePeriodicResync is true.
    /// </summary>
    private async Task PeriodicResyncAsync()
    {
        if (_disposed || _rootNodeId is null)
        {
            return;
        }

        try
        {
            _logger.LogDebug("Starting periodic address space resync...");

            await _syncLock.WaitAsync(_disposalCts.Token).ConfigureAwait(false);
            try
            {
                // 1. Browse current remote address space
                var currentRemoteNodes = new HashSet<NodeId>();
                await BrowseAddressSpaceRecursiveAsync(_rootNodeId, currentRemoteNodes, _disposalCts.Token).ConfigureAwait(false);

                // 2. Find added nodes (in remote but not in known)
                var addedNodes = currentRemoteNodes.Except(_knownRemoteNodeIds).ToList();

                // 3. Find removed nodes (in known but not in remote)
                var removedNodes = _knownRemoteNodeIds.Except(currentRemoteNodes).ToList();

                _logger.LogDebug(
                    "Periodic resync found {AddedCount} added nodes and {RemovedCount} removed nodes",
                    addedNodes.Count,
                    removedNodes.Count);

                // 4. Process removals
                foreach (var nodeId in removedNodes)
                {
                    try
                    {
                        await _strategy.OnRemoteNodeRemovedAsync(nodeId, _disposalCts.Token).ConfigureAwait(false);
                        _knownRemoteNodeIds.Remove(nodeId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process removed node {NodeId} during periodic resync", nodeId);
                    }
                }

                // 5. Process additions
                foreach (var nodeId in addedNodes)
                {
                    try
                    {
                        // Browse the node to get its details
                        var children = await _strategy.BrowseNodeAsync(nodeId, _disposalCts.Token).ConfigureAwait(false);

                        // Create a reference description for the added node
                        var nodeDescription = new ReferenceDescription
                        {
                            NodeId = new ExpandedNodeId(nodeId),
                            NodeClass = NodeClass.Object
                        };

                        // Find parent - for simplicity, assume direct child of root
                        // In a more complete implementation, we would track parent relationships
                        await _strategy.OnRemoteNodeAddedAsync(
                            nodeDescription,
                            _rootNodeId,
                            _disposalCts.Token).ConfigureAwait(false);

                        _knownRemoteNodeIds.Add(nodeId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process added node {NodeId} during periodic resync", nodeId);
                    }
                }

                // 6. Update known nodes
                _knownRemoteNodeIds.Clear();
                foreach (var nodeId in currentRemoteNodes)
                {
                    _knownRemoteNodeIds.Add(nodeId);
                }

                _logger.LogDebug("Periodic resync completed successfully");
            }
            finally
            {
                _syncLock.Release();
            }
        }
        catch (OperationCanceledException) when (_disposalCts.IsCancellationRequested)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Periodic address space resync failed.");
        }
    }

    /// <summary>
    /// Recursively browses the OPC UA address space starting from the given node.
    /// </summary>
    private async Task BrowseAddressSpaceRecursiveAsync(
        NodeId nodeId,
        HashSet<NodeId> collectedNodes,
        CancellationToken cancellationToken,
        int depth = 0)
    {
        // Prevent infinite recursion with reasonable depth limit
        if (depth > 10)
        {
            return;
        }

        try
        {
            var children = await _strategy.BrowseNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);

            foreach (var child in children)
            {
                var childNodeId = ExpandedNodeId.ToNodeId(child.NodeId, null);
                if (childNodeId is not null && collectedNodes.Add(childNodeId))
                {
                    if (child.NodeClass == NodeClass.Object)
                    {
                        await BrowseAddressSpaceRecursiveAsync(
                            childNodeId,
                            collectedNodes,
                            cancellationToken,
                            depth + 1).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to browse node {NodeId} during periodic resync", nodeId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Cancel any in-flight operations first
        _disposalCts.Cancel();

        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectAttached -= OnSubjectAttached;
            _lifecycleInterceptor.SubjectDetached -= OnSubjectDetached;
        }

        _periodicResyncTimer?.Dispose();

        // Clear all mappings to prevent memory leaks
        _strategy.ClearAllMappings();
        _processedSubjects.Clear();

        _disposalCts.Dispose();
        _syncLock.Dispose();
    }
}
