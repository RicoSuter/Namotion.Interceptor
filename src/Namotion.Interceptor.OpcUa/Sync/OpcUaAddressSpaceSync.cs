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

    private LifecycleInterceptor? _lifecycleInterceptor;
    private Timer? _periodicResyncTimer;
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
    public void Initialize(IInterceptorSubject rootSubject)
    {
        if (!_configuration.EnableLiveSync)
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

        if (_configuration.EnablePeriodicResync)
        {
            _periodicResyncTimer = new Timer(
                _ => _ = PeriodicResyncAsync(),
                null,
                _configuration.PeriodicResyncInterval,
                _configuration.PeriodicResyncInterval);

            _logger.LogInformation(
                "Periodic address space resync enabled with interval: {Interval}",
                _configuration.PeriodicResyncInterval);
        }
    }

    /// <summary>
    /// Handles local subject attached events.
    /// Delegates to strategy to create corresponding OPC UA structures.
    /// </summary>
    private void OnSubjectAttached(SubjectLifecycleChange change)
    {
        // Fire and forget - don't block lifecycle event, but capture exceptions in the async method
        _ = Task.Run(() => OnSubjectAttachedAsync(change));
    }

    private async Task OnSubjectAttachedAsync(SubjectLifecycleChange change)
    {
        await _syncLock.WaitAsync().ConfigureAwait(false);
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

            await _strategy.OnSubjectAttachedAsync(change.Subject, CancellationToken.None).ConfigureAwait(false);

            _logger.LogDebug("Subject sync completed for {SubjectType}.", change.Subject.GetType().Name);
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
        // Fire and forget - don't block lifecycle event, but capture exceptions in the async method
        _ = Task.Run(() => OnSubjectDetachedAsync(change));
    }

    private async Task OnSubjectDetachedAsync(SubjectLifecycleChange change)
    {
        await _syncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _processedSubjects.Remove(change.Subject);

            _logger.LogDebug(
                "Local subject detached: {SubjectType}. Syncing to OPC UA...",
                change.Subject.GetType().Name);

            await _strategy.OnSubjectDetachedAsync(change.Subject, CancellationToken.None).ConfigureAwait(false);

            _logger.LogDebug("Subject detach sync completed for {SubjectType}.", change.Subject.GetType().Name);
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
        try
        {
            _logger.LogDebug("Starting periodic address space resync...");

            // TODO: Implement periodic resync logic in Phase 5
            // This will involve:
            // 1. Browse entire local subject graph
            // 2. Browse entire remote OPC UA address space
            // 3. Compare and identify differences
            // 4. Call OnRemoteNodeAddedAsync/OnRemoteNodeRemovedAsync for differences

            _logger.LogDebug("Periodic resync completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Periodic address space resync failed.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectAttached -= OnSubjectAttached;
            _lifecycleInterceptor.SubjectDetached -= OnSubjectDetached;
        }

        _periodicResyncTimer?.Dispose();
        _syncLock.Dispose();
    }
}
