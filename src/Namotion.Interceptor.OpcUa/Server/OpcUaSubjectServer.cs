using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServer : BackgroundService, IOpcUaSubjectServer, ISubjectConnector, IFaultInjectable
{
    // Per-instance key so multiple servers can expose the same property tree without
    // overwriting each other's BaseDataVariableState reference on shared properties.
    internal string OpcUaVariableKey { get; } = "OpcUaVariable:" + Guid.NewGuid();

    private readonly IInterceptorSubject _subject;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly OpcUaServerConfiguration _configuration;

    private LifecycleInterceptor? _lifecycleInterceptor;
    private volatile OpcUaStandardServer? _server;
    private volatile bool _isForceKill;
    private volatile CancellationTokenSource? _forceKillCts;
    private int _consecutiveFailures;
    private DateTimeOffset? _startTime;
    private Exception? _lastError;

    /// <summary>
    /// Maps detached subjects to their metadata, captured during OnSubjectDetaching.
    /// When structural sync is enabled, node removal is deferred to HandleStructuralChange.
    /// This dictionary bridges the gap: the lifecycle event captures metadata (while the
    /// subject is still registered) and HandleStructuralChange consumes it.
    /// </summary>
    private readonly Dictionary<IInterceptorSubject, PendingDetachInfo> _pendingDetachInfo = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Metadata captured when a subject is detaching, used for deferred node removal.
    /// </summary>
    private sealed class PendingDetachInfo
    {
        public required NodeId NodeId { get; init; }

        /// <summary>
        /// Variable node references keyed by property BrowseName, captured before detach.
        /// Used for in-place reference replacement to rebind monitored items.
        /// </summary>
        public required Dictionary<string, BaseDataVariableState> VariableNodes { get; init; }
    }

    internal ThroughputCounter IncomingThroughput { get; } = new();
    internal ThroughputCounter OutgoingThroughput { get; } = new();

    /// <inheritdoc />
    public IInterceptorSubject RootSubject => _subject;

    /// <inheritdoc />
    Task IFaultInjectable.InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken)
    {
        // For a multi-connection server, all fault types are treated as force-kill.
        // There's no meaningful "soft disconnect" when the server has multiple clients.
        _isForceKill = true;
        try { _forceKillCts?.Cancel(); }
        catch (ObjectDisposedException) { /* CTS disposed between loop iterations */ }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public OpcUaServerDiagnostics Diagnostics { get; }

    /// <inheritdoc />
    public StandardServer? CurrentServer => _server;

    /// <summary>
    /// Gets a value indicating whether the server is running.
    /// </summary>
    internal bool IsRunning => _server?.CurrentInstance != null;

    /// <summary>
    /// Gets the number of active sessions.
    /// </summary>
    internal int ActiveSessionCount => _server?.CurrentInstance?.SessionManager?.GetSessions()?.Count ?? 0;

    /// <summary>
    /// Gets the server start time.
    /// </summary>
    internal DateTimeOffset? StartTime => _startTime;

    /// <summary>
    /// Gets the last error.
    /// </summary>
    internal Exception? LastError => _lastError;

    /// <summary>
    /// Gets the consecutive failure count.
    /// </summary>
    internal int ConsecutiveFailures => _consecutiveFailures;

    public OpcUaSubjectServer(
        IInterceptorSubject subject,
        OpcUaServerConfiguration configuration,
        ILogger logger)
    {
        _subject = subject;
        _context = subject.Context;
        _logger = logger;
        _configuration = configuration;
        Diagnostics = new OpcUaServerDiagnostics(this);
    }

    /// <inheritdoc />
    public bool TryGetVariableNode(PropertyReference property, [NotNullWhen(true)] out BaseDataVariableState? variable)
    {
        if (property.TryGetPropertyData(OpcUaVariableKey, out var data) && data is BaseDataVariableState resolved)
        {
            variable = resolved;
            return true;
        }

        variable = null;
        return false;
    }

    private bool IsPropertyIncluded(PropertyReference propertyReference)
    {
        return propertyReference.TryGetRegisteredProperty() is { } property &&
               property.IsPropertyIncluded(_configuration.NodeMapper);
    }

    private ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var server = _server;
        var currentInstance = server?.CurrentInstance;
        if (currentInstance == null)
        {
            return ValueTask.CompletedTask;
        }

        var nodeManagerLock = server?.NodeManagerLock;
        if (nodeManagerLock == null)
        {
            return ValueTask.CompletedTask;
        }

        var nodeManager = server?.GetNodeManager();
        var span = changes.Span;

        lock (nodeManagerLock)
        {
            for (var i = 0; i < span.Length; i++)
            {
                var change = span[i];
                var registeredProperty = change.Property.TryGetRegisteredProperty();
                if (registeredProperty is null)
                {
                    continue;
                }

                if (registeredProperty.IsSubjectCollection || registeredProperty.IsSubjectDictionary || registeredProperty.IsSubjectReference)
                {
                    if (nodeManager is not null && _configuration.EnableStructureSynchronization)
                    {
                        HandleStructuralChange(change, registeredProperty, nodeManager);
                    }
                }
                else if (change.Property.TryGetPropertyData(OpcUaVariableKey, out var data) &&
                         data is BaseDataVariableState node)
                {
                    var value = change.GetNewValue<object?>();
                    var convertedValue = _configuration.ValueConverter
                        .ConvertToNodeValue(value, registeredProperty);

                    node.Value = convertedValue;
                    node.Timestamp = change.ChangedTimestamp.UtcDateTime;
                    node.ClearChangeMasks(currentInstance.DefaultSystemContext, false);
                }
            }
        }

        OutgoingThroughput.Add(span.Length);
        return ValueTask.CompletedTask;
    }

    private void HandleStructuralChange(SubjectPropertyChange change, RegisteredSubjectProperty registeredProperty, CustomNodeManager nodeManager)
    {
        var oldSubjects = OpcUaStructuralChangeHelper.ExtractSubjects(change.GetOldValue<object?>());
        var newSubjects = OpcUaStructuralChangeHelper.ExtractSubjects(change.GetNewValue<object?>());

        _logger.LogInformation(
            "HandleStructuralChange for property {PropertyName}: old={OldCount} subjects, new={NewCount} subjects.",
            registeredProperty.Name, oldSubjects.Count, newSubjects.Count);

        var (addedSubjects, removedSubjects) = OpcUaStructuralChangeHelper.ComputeSubjectDiff(oldSubjects, newSubjects);

        // For reference properties (single subject), try in-place replacement.
        // This avoids deleting/recreating nodes at the same NodeId path, which would
        // orphan existing monitored items on connected clients.
        if (registeredProperty.IsSubjectReference &&
            removedSubjects.Count == 1 && addedSubjects.Count == 1)
        {
            var oldSubject = removedSubjects[0].Subject;
            var newSubject = addedSubjects[0].Subject;

            PendingDetachInfo? detachInfo;
            lock (_pendingDetachInfo)
            {
                _pendingDetachInfo.Remove(oldSubject, out detachInfo);
            }

            if (detachInfo is not null &&
                nodeManager.TryReplaceSubjectMapping(oldSubject, newSubject))
            {
                // Rebind variable nodes and push updated values using captured references
                var newRegistered = newSubject.TryGetRegisteredSubject();
                if (newRegistered is not null)
                {
                    RebindVariableNodesFromDetachInfo(
                        detachInfo.VariableNodes, newRegistered, nodeManager);

                    // Recursively handle child reference replacements (e.g., Person.Address)
                    RebindChildReferences(oldSubject, newSubject, nodeManager);
                }

                _logger.LogInformation(
                    "HandleStructuralChange: in-place replaced reference for property {PropertyName}.",
                    registeredProperty.Name);
                return;
            }
        }

        // General case: remove old subjects, then add new ones.
        // Do removals FIRST so that NodeIds are freed for reuse.
        foreach (var (subject, _) in removedSubjects)
        {
            // Try to get NodeId from the subject's registration first.
            // If the subject was already unregistered by the lifecycle interceptor,
            // fall back to the NodeId captured in OnSubjectDetaching.
            NodeId? nodeId = null;
            var registeredSubject = subject.TryGetRegisteredSubject();
            if (registeredSubject is not null)
            {
                nodeManager.TryGetNodeId(registeredSubject, out nodeId);
            }

            if (nodeId is null)
            {
                lock (_pendingDetachInfo)
                {
                    if (_pendingDetachInfo.Remove(subject, out var info))
                    {
                        nodeId = info.NodeId;
                    }
                }
            }

            nodeManager.RemoveSubjectNodes(subject);

            if (nodeId is not null)
            {
                _logger.LogInformation(
                    "HandleStructuralChange: firing NodeDeleted for {NodeId} on property {PropertyName}.",
                    nodeId, registeredProperty.Name);
                nodeManager.FireModelChangeEvent(ModelChangeStructureVerbMask.NodeDeleted, nodeId);
            }
            else
            {
                _logger.LogWarning(
                    "HandleStructuralChange: removed subject has no NodeId, cannot fire NodeDeleted for property {PropertyName}.",
                    registeredProperty.Name);
            }
        }

        // Add new subjects.
        foreach (var (subject, index) in addedSubjects)
        {
            var lifecycleChange = new SubjectLifecycleChange
            {
                Subject = subject,
                Property = change.Property,
                Index = index,
                ReferenceCount = 0
            };

            var createdNode = nodeManager.CreateDynamicSubjectNodes(lifecycleChange);
            if (createdNode is not null)
            {
                // Force data change notifications for variable nodes of the new subject.
                nodeManager.ClearChangeMasksForSubject(subject);

                _logger.LogInformation(
                    "HandleStructuralChange: firing NodeAdded for {NodeId} on property {PropertyName}.",
                    createdNode.NodeId, registeredProperty.Name);
                nodeManager.FireModelChangeEvent(ModelChangeStructureVerbMask.NodeAdded, createdNode.NodeId);
            }
        }
    }

    private void RebindVariableNodesFromDetachInfo(
        Dictionary<string, BaseDataVariableState> capturedNodes,
        RegisteredSubject newRegistered,
        CustomNodeManager nodeManager)
    {
        var currentInstance = _server?.CurrentInstance;

        foreach (var newProperty in newRegistered.Properties)
        {
            var propertyName = newProperty.ResolvePropertyName(_configuration.NodeMapper);
            if (propertyName is null || newProperty.CanContainSubjects)
            {
                continue;
            }

            if (!capturedNodes.TryGetValue(propertyName, out var variableNode))
            {
                continue;
            }

            // Rebind the variable node to the new property
            newProperty.Reference.SetPropertyData(OpcUaVariableKey, variableNode);
            variableNode.Handle = newProperty.Reference;

            // Update value and push to clients via ClearChangeMasks
            var value = _configuration.ValueConverter.ConvertToNodeValue(newProperty.GetValue(), newProperty);
            variableNode.Value = value;
            variableNode.Timestamp = DateTime.UtcNow;

            if (currentInstance is not null)
            {
                variableNode.ClearChangeMasks(currentInstance.DefaultSystemContext, false);
            }
        }
    }

    /// <summary>
    /// Recursively rebinds child reference subjects. When a parent is replaced in-place,
    /// its child references also need rebinding. Each child subject has its own
    /// PendingDetachInfo captured during OnSubjectDetaching.
    /// </summary>
    private void RebindChildReferences(
        IInterceptorSubject oldParent,
        IInterceptorSubject newParent,
        CustomNodeManager nodeManager)
    {
        var newRegistered = newParent.TryGetRegisteredSubject();
        if (newRegistered is null)
        {
            return;
        }

        foreach (var newProperty in newRegistered.Properties)
        {
            if (!newProperty.IsSubjectReference)
            {
                continue;
            }

            var newChild = newProperty.Children.SingleOrDefault().Subject;
            if (newChild is null)
            {
                continue;
            }

            // Find the matching old child's detach info by scanning _pendingDetachInfo
            // for entries whose NodeId matches the expected child path
            PendingDetachInfo? childDetachInfo = null;
            IInterceptorSubject? oldChildSubject = null;

            lock (_pendingDetachInfo)
            {
                foreach (var kvp in _pendingDetachInfo)
                {
                    // Use the first pending detach info that successfully maps to the new child
                    if (nodeManager.TryReplaceSubjectMapping(kvp.Key, newChild))
                    {
                        childDetachInfo = kvp.Value;
                        oldChildSubject = kvp.Key;
                        break;
                    }
                }

                if (oldChildSubject is not null)
                {
                    _pendingDetachInfo.Remove(oldChildSubject);
                }
            }

            if (childDetachInfo is not null)
            {
                var childRegistered = newChild.TryGetRegisteredSubject();
                if (childRegistered is not null)
                {
                    RebindVariableNodesFromDetachInfo(
                        childDetachInfo.VariableNodes, childRegistered, nodeManager);

                    // Recurse for nested children
                    if (oldChildSubject is not null)
                    {
                        RebindChildReferences(oldChildSubject, newChild, nodeManager);
                    }
                }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _context.WithRegistry();

        _lifecycleInterceptor = _context.TryGetLifecycleInterceptor();
        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectDetaching += OnSubjectDetaching;
        }

        try
        {
            await ExecuteServerLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            if (_lifecycleInterceptor is not null)
            {
                _lifecycleInterceptor.SubjectDetaching -= OnSubjectDetaching;
            }
        }
    }

    private async Task ExecuteServerLoopAsync(CancellationToken stoppingToken)
    {
        // Reset failure counter on fresh start so that accumulated failures from
        // previous stop/start cycles don't cause excessive backoff delays.
        _consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _forceKillCts = cts;
            var linkedToken = cts.Token;

            var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);

            if (_configuration.CleanCertificateStore)
            {
                CleanCertificateStore(application);
            }

            var server = new OpcUaStandardServer(_subject, this, _configuration, _logger);
            try
            {
                try
                {
                    _server = server;

                    // Create the ChangeQueueProcessor (and its subscription) BEFORE starting the server.
                    // This ensures property changes during OPC UA node creation are captured in the queue
                    // and not lost in the gap between node creation and processing start.
                    using var changeQueueProcessor = new ChangeQueueProcessor(
                        source: this, _context,
                        propertyFilter: IsPropertyIncluded, writeHandler: WriteChangesAsync,
                        _configuration.BufferTime, _logger);

                    await application.CheckApplicationInstanceCertificatesAsync(true, ct: linkedToken).ConfigureAwait(false);
                    await application.StartAsync(server).ConfigureAwait(false);

                    _startTime = DateTimeOffset.UtcNow;
                    _consecutiveFailures = 0;
                    _lastError = null;

                    await changeQueueProcessor.ProcessAsync(linkedToken);
                }
                finally
                {
                    _startTime = null;
                    var serverToClean = _server;
                    _server = null;
                    serverToClean?.ClearPropertyData();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown takes priority over force-kill (checked first intentionally).
                // If both stoppingToken and _isForceKill are set, we exit cleanly rather than restart.
            }
            catch (OperationCanceledException) when (_isForceKill)
            {
                // Force-kill: CTS was cancelled by KillAsync
                _logger.LogWarning("OPC UA server force-killed. Restarting...");
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _lastError = ex;
                _logger.LogError(ex, "Failed to start OPC UA server (attempt {Attempt}).", _consecutiveFailures);

                // Exponential backoff with jitter: 1s, 2s, 4s, 8s, 16s, 30s (capped) + 0-2s random jitter
                // Jitter prevents thundering herd when multiple servers fail simultaneously
                var baseDelay = Math.Min(Math.Pow(2, _consecutiveFailures - 1), 30);
                var jitter = Random.Shared.NextDouble() * 2;
                await Task.Delay(TimeSpan.FromSeconds(baseDelay + jitter), stoppingToken);
            }
            finally
            {
                try
                {
                    if (_isForceKill)
                    {
                        // Force-kill: close transport listeners immediately so clients see
                        // an abrupt connection loss (realistic crash simulation).
                        if (application.Server is OpcUaStandardServer s)
                        {
                            s.CloseTransportListeners();
                        }
                    }

                    // Always run ShutdownServerAsync to ensure the SDK's internal tasks
                    // (SubscriptionManager publish/refresh threads) are properly signaled
                    // to exit via OnServerStoppingAsync. Without StopAsync, these
                    // fire-and-forget tasks keep the entire server object graph alive as
                    // GC roots, causing ~8-16 MB leak per server restart.
                    // On force-kill the transport is already dead, so this only cleans up
                    // internal state — it doesn't change what clients observe.
                    await ShutdownServerAsync(application).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to shutdown OPC UA server.");
                }
                finally
                {
                    _isForceKill = false;

                    // Dispose transport listeners before server.Dispose().
                    // The SDK's StopAsync calls Close() on each listener (which only
                    // stops accepting connections) then clears the TransportListeners list.
                    // When server.Dispose() later tries to Dispose() listeners, the list
                    // is empty — so TcpTransportListener.Dispose() never runs, leaving
                    // per-client channel sockets and inactivity timers alive as GC roots
                    // that retain the entire server object graph.
                    try { server.DisposeTransportListeners(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Error disposing transport listeners."); }

                    try { server.Dispose(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Error disposing OPC UA server."); }

                    cts.Dispose();
                }
            }
        }
    }

    private async Task ShutdownServerAsync(ApplicationInstance application)
    {
        try
        {
            if (application.Server is OpcUaStandardServer server)
            {
                // Close transport listeners first to stop accepting new connections.
                // Without this, clients reconnect during shutdown faster than sessions
                // can be closed, causing StopAsync to hang indefinitely.
                server.CloseTransportListeners();

                if (server.CurrentInstance?.SessionManager is { } sessionManager)
                {
                    var sessions = sessionManager.GetSessions();
                    foreach (var session in sessions)
                    {
                        try { session.Close(); } catch (Exception ex) { _logger.LogDebug(ex, "Error closing session during shutdown."); }
                    }
                }
            }

            // Timeout prevents hang when clients keep reconnecting during shutdown
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await application.StopAsync().AsTask().WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                _logger.LogWarning("OPC UA server shutdown timed out after 10s. Continuing with disposal.");
            }
        }
        catch (ServiceResultException e) when (e.StatusCode == StatusCodes.BadServerHalted)
        {
            // Server already halted
        }
    }

    private void CleanCertificateStore(ApplicationInstance application)
    {
        var path = application
            .ApplicationConfiguration
            .SecurityConfiguration
            .ApplicationCertificate
            .StorePath;

        if (string.IsNullOrEmpty(path))
        {
            _logger.LogWarning("Certificate store path is empty, skipping cleanup.");
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                _logger.LogDebug("Cleaned certificate store at {Path}.", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean certificate store at {Path}. Continuing with existing certificates.", path);
        }
    }

    internal void UpdateProperty(PropertyReference property, DateTimeOffset changedTimestamp, object? value)
    {
        IncomingThroughput.Add(1);
        var receivedTimestamp = DateTimeOffset.UtcNow;

        var registeredProperty = property.TryGetRegisteredProperty();
        if (registeredProperty is not null)
        {
            var convertedValue = _configuration.ValueConverter.ConvertToPropertyValue(value, registeredProperty);

            try
            {
                property.SetValueFromSource(this, changedTimestamp, receivedTimestamp, convertedValue);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to apply property update from OPC UA client.");
            }
        }
    }

    // Node creation is handled exclusively by HandleStructuralChange via the
    // ChangeQueueProcessor (reacting to property changes). Lifecycle events
    // (SubjectAttached/SubjectDetaching) are only used for metadata capture
    // and cleanup, never for node creation/removal.

    private void OnSubjectDetaching(SubjectLifecycleChange change)
    {
        var server = _server;
        if (server is null)
        {
            return;
        }

        if (_configuration.EnableStructureSynchronization)
        {
            try
            {
                var nodeManager = server.GetNodeManager();
                if (nodeManager is not null)
                {
                    var registeredSubject = change.Subject.TryGetRegisteredSubject();
                    if (registeredSubject is not null &&
                        nodeManager.TryGetNodeId(registeredSubject, out var nodeId) &&
                        nodeId is not null)
                    {
                        var variableNodes = new Dictionary<string, BaseDataVariableState>();
                        foreach (var property in registeredSubject.Properties)
                        {
                            if (property.Reference.TryGetPropertyData(OpcUaVariableKey, out var data) &&
                                data is BaseDataVariableState variableNode)
                            {
                                var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
                                if (propertyName is not null)
                                {
                                    variableNodes[propertyName] = variableNode;
                                }
                            }
                        }

                        lock (_pendingDetachInfo)
                        {
                            _pendingDetachInfo[change.Subject] = new PendingDetachInfo
                            {
                                NodeId = nodeId,
                                VariableNodes = variableNodes
                            };
                        }
                    }
            }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to capture PendingDetachInfo for subject {SubjectType}", change.Subject.GetType().Name);
            }
            return;
        }

        server.RemoveSubjectNodes(change.Subject);
    }
}
