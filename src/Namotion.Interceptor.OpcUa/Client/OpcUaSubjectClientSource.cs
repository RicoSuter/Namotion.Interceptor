using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal sealed class OpcUaSubjectClientSource : SubjectSourceBase, IOpcUaSubjectClientSource, IFaultInjectable, IAsyncDisposable
{
    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;

    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly SubscriptionHealthMonitor _subscriptionHealthMonitor;

    private volatile SessionManager? _sessionManager;
    private volatile SubjectPropertyWriter? _propertyWriter;
    private OutboundWriter? _writer;

    private readonly SemaphoreSlim _structureLock = new(1, 1);
    private volatile CancellationTokenSource? _reconnectCts; // Cancelled by KillAsync to abort in-flight reconnection
    private volatile CancellationTokenSource? _loadCts; // Cancelled by DisposeAsync to abort an in-flight initial load
    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)

    private volatile bool _isStarted;
    private long _reconnectStartedTimestamp; // 0 = not reconnecting, otherwise Stopwatch timestamp when reconnection started (for stall detection)
    private Exception? _lastError;

    internal string OpcUaNodeIdKey { get; } = "OpcUaNodeId:" + Guid.NewGuid();

    internal SessionManager? SessionManager => _sessionManager;
    internal SourceOwnershipManager Ownership => _ownership;

    internal ReconnectionMetrics ReconnectionMetrics { get; } = new();
    internal ThroughputCounter IncomingThroughput { get; } = new();
    internal ThroughputCounter OutgoingThroughput { get; } = new();
    internal Exception? LastError => Volatile.Read(ref _lastError);

    internal void ClearLastError() => Volatile.Write(ref _lastError, null);

    /// <inheritdoc />
    public override int WriteBatchSize => _writer?.WriteBatchSize ?? 0;

    /// <inheritdoc />
    public OpcUaClientDiagnostics Diagnostics { get; }

    /// <inheritdoc />
    public ISession? CurrentSession => _sessionManager?.CurrentSession;

    /// <inheritdoc />
    public event EventHandler<OpcUaCurrentSessionChangedEventArgs>? CurrentSessionChanged;

    public OpcUaSubjectClientSource(IInterceptorSubject subject, OpcUaClientConfiguration configuration, ILogger logger)
        : base(subject.Context, logger, configuration.BufferTime, configuration.RetryTime, configuration.WriteRetryQueueSize)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        configuration.Validate();

        _subject = subject;
        _logger = logger;
        _configuration = configuration;

        _ownership = new SourceOwnershipManager(
            this,
            onReleasing: property =>
            {
                // Unregister from ReadAfterWriteManager FIRST (uses NodeId)
                if (property.TryGetPropertyData(OpcUaNodeIdKey, out var nodeIdObj) &&
                    nodeIdObj is NodeId nodeId)
                {
                    _sessionManager?.ReadAfterWriteManager?.UnregisterProperty(nodeId);
                }
                property.RemovePropertyData(OpcUaNodeIdKey);
            },
            onSubjectDetaching: OnSubjectDetaching);

        _subjectLoader = new OpcUaSubjectLoader(subject, configuration, _ownership, this, logger);
        _subscriptionHealthMonitor = new SubscriptionHealthMonitor(logger);

        Diagnostics = new OpcUaClientDiagnostics(this);
    }

    private void OnSubjectDetaching(IInterceptorSubject subject)
    {
        RemoveItemsForSubject(subject);
    }

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject => _subject;

    protected override async Task<IAsyncDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        Reset();

        _propertyWriter = propertyWriter;
        _logger.LogInformation("Connecting to OPC UA server at {ServerUrl}.", _configuration.ServerUrl);

        _sessionManager = new SessionManager(this, propertyWriter, _configuration, _logger);
        _writer = new OutboundWriter(_sessionManager, _configuration, OpcUaNodeIdKey, OutgoingThroughput, _logger);

        try
        {
            var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);
            var session = await _sessionManager.CreateSessionAsync(application, _configuration, cancellationToken).ConfigureAwait(false);

            ReconnectionMetrics.RecordInitialConnection();
            _logger.LogInformation("Connected to OPC UA server successfully.");

            // Linked CTS so DisposeAsync can abort an in-flight initial load: the load
            // holds _structureLock across network I/O and DisposeAsync waits on that lock
            // uncancellably (mirrors the _reconnectCts pattern for reconnects).
            using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loadCts = loadCts;
            try
            {
                await _structureLock.WaitAsync(loadCts.Token).ConfigureAwait(false);
                try
                {
                    var loadStopwatch = Stopwatch.StartNew();

                    var rootNode = await TryGetRootNodeAsync(session, loadCts.Token).ConfigureAwait(false);
                    if (rootNode is not null)
                    {
                        var monitoredItems = await _subjectLoader.LoadSubjectAsync(_subject, rootNode, session, loadCts.Token).ConfigureAwait(false);
                        if (monitoredItems.Count > 0)
                        {
                            await _sessionManager.CreateSubscriptionsAsync(monitoredItems, session, loadCts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogWarning("No OPC UA monitored items found.");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Connected to OPC UA server successfully but could not find root node.");
                    }

                    _logger.LogInformation("OPC UA subject loading and subscription setup completed in {ElapsedMs}ms.", loadStopwatch.ElapsedMilliseconds);
                }
                finally
                {
                    _structureLock.Release();
                }
            }
            finally
            {
                _loadCts = null;
            }

            _isStarted = true;
            Volatile.Write(ref _lastError, null);

            var sessionManagerForLifetime = _sessionManager;
            return BackgroundTaskLifetime.Start(
                cancellationToken,
                _logger,
                RunHealthCheckLoopAsync,
                async () =>
                {
                    try { await sessionManagerForLifetime.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogWarning(ex, "OPC UA session manager threw during listen-lifetime disposal."); }
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _disposed == 1)
        {
            await CleanupSessionManagerAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastError, ex);
            await CleanupSessionManagerAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task CleanupSessionManagerAsync()
    {
        var sessionManager = _sessionManager;
        if (sessionManager is not null)
        {
            try { await sessionManager.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "OPC UA session manager threw during listen-failure cleanup."); }
            _sessionManager = null;
        }
    }

    /// <inheritdoc />
    public override async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        var ownedProperties = GetOwnedPropertiesWithNodeIds();
        if (ownedProperties.Count == 0)
        {
            return null;
        }

        var session = _sessionManager?.CurrentSession;
        if (session is null)
        {
            throw new InvalidOperationException("No active OPC UA session available.");
        }

        var itemCount = ownedProperties.Count;
        var readValues = new ReadValueIdCollection(itemCount);
        for (var i = 0; i < itemCount; i++)
        {
            readValues.Add(new ReadValueId
            {
                NodeId = ownedProperties[i].NodeId,
                AttributeId = Opc.Ua.Attributes.Value
            });
        }

        // ReadNodesAsync pads short responses to the requested length, so positional
        // alignment between allResults[i] and ownedProperties[i] is guaranteed.
        var allResults = await session.ReadNodesAsync(readValues, TimestampsToReturn.Source, _logger, cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<RegisteredSubjectProperty, DataValue>(itemCount);
        for (var i = 0; i < itemCount; i++)
        {
            if (StatusCode.IsGood(allResults[i].StatusCode))
            {
                result[ownedProperties[i].Property] = allResults[i];
            }
        }

        // Best-effort: any non-good status (e.g. a not-ready BadWaitingForInitialData) is
        // left unset for the subscription to backfill. A single bad node must not abort
        // the load or trigger a reconnect.
        var successCount = result.Count;
        _logger.LogInformation(
            "Read {Total} OPC UA nodes from server ({Successful} good, {Skipped} skipped with non-good status).",
            itemCount, successCount, itemCount - successCount);
        return () =>
        {
            foreach (var (property, dataValue) in result)
            {
                var value = _configuration.ValueConverter.ConvertToPropertyValue(dataValue.Value, property);
                property.SetValueFromSource(this, dataValue.SourceTimestamp, null, value);
            }

            _logger.LogInformation("Updated {Count} properties with OPC UA node values.", successCount);
        };
    }

    /// <inheritdoc />
    public bool TryGetNodeId(PropertyReference property, [NotNullWhen(true)] out NodeId? nodeId)
    {
        if (property.TryGetPropertyData(OpcUaNodeIdKey, out var data) && data is NodeId resolved)
        {
            nodeId = resolved;
            return true;
        }

        nodeId = null;
        return false;
    }

    /// <summary>
    /// Gets all owned properties that have OPC UA NodeIds, for reading or recreating subscriptions.
    /// This avoids holding onto heavy MonitoredItem objects and allows GC of SDK objects.
    /// </summary>
    private List<(RegisteredSubjectProperty Property, NodeId NodeId)> GetOwnedPropertiesWithNodeIds()
    {
        var result = new List<(RegisteredSubjectProperty, NodeId)>();
        foreach (var property in _ownership.Properties)
        {
            if (property.TryGetPropertyData(OpcUaNodeIdKey, out var nodeIdObj) && 
                nodeIdObj is NodeId nodeId &&
                property.GetRegisteredProperty() is { } registeredProperty)
            {
                result.Add((registeredProperty, nodeId));
            }
        }
        return result;
    }

    /// <summary>
    /// Creates MonitoredItems for reconnection from owned properties.
    /// This recreates SDK objects on demand instead of holding them in memory.
    /// Called by SessionManager when subscription transfer fails after server restart.
    /// </summary>
    private IReadOnlyList<MonitoredItem> CreateMonitoredItemsForReconnection()
    {
        var ownedProperties = GetOwnedPropertiesWithNodeIds();
        var monitoredItems = new List<MonitoredItem>(ownedProperties.Count);

        foreach (var (property, nodeId) in ownedProperties)
        {
            var monitoredItem = MonitoredItemFactory.Create(_configuration, nodeId, property, _subject);
            monitoredItems.Add(monitoredItem);
        }

        return monitoredItems;
    }

    private async Task RunHealthCheckLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sessionManager = _sessionManager;
                var propertyWriter = _propertyWriter;
                if (sessionManager is not null && propertyWriter is not null && _isStarted)
                {
                    if (sessionManager.HasSessionsToDispose)
                    {
                        await sessionManager.DisposePendingSessionsAsync(stoppingToken).ConfigureAwait(false);
                    }

                    var isReconnecting = sessionManager.IsReconnecting;
                    var currentSession = sessionManager.CurrentSession;
                    var sessionIsConnected = currentSession?.Connected ?? false;

                    if (currentSession is not null && sessionIsConnected && !isReconnecting)
                    {
                        if (await HandleHealthySessionAsync(sessionManager, stoppingToken).ConfigureAwait(false))
                        {
                            continue;
                        }
                    }
                    else if (!isReconnecting)
                    {
                        await HandleDeadSessionAsync(currentSession, sessionIsConnected, stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await HandleReconnectionStallDetectionAsync(sessionManager, currentSession, sessionIsConnected, stoppingToken).ConfigureAwait(false);
                    }
                }

                await Task.Delay(_configuration.SubscriptionHealthCheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check or session restart. Retrying after delay.");
                try { await Task.Delay(_configuration.SubscriptionHealthCheckInterval, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        _logger.LogInformation("OPC UA client health loop has stopped.");
    }

    private async Task<bool> HandleHealthySessionAsync(SessionManager sessionManager, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);

        await sessionManager.PerformFullStateSyncIfNeededAsync(cancellationToken).ConfigureAwait(false);

        if (sessionManager.SubscriptionManager.HasStoppedPublishing)
        {
            _logger.LogWarning(
                "OPC UA subscription has stopped publishing. Starting manual reconnection to recover notification flow...");
            await ReconnectSessionAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        await _subscriptionHealthMonitor.CheckAndHealSubscriptionsAsync(
            sessionManager.Subscriptions,
            cancellationToken).ConfigureAwait(false);

        await sessionManager.SubscriptionManager
            .EscalatePersistentlyFailedItemsAsync(cancellationToken).ConfigureAwait(false);

        return false;
    }

    private async Task HandleDeadSessionAsync(Session? currentSession, bool sessionIsConnected, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);
        _logger.LogWarning(
            "OPC UA session is dead (session={HasSession}, connected={IsConnected}). " +
            "Starting manual reconnection...",
            currentSession is not null,
            sessionIsConnected);

        await ReconnectSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleReconnectionStallDetectionAsync(SessionManager sessionManager, Session? currentSession, bool sessionIsConnected, CancellationToken cancellationToken)
    {
        var startedAt = Interlocked.Read(ref _reconnectStartedTimestamp);
        if (startedAt == 0)
        {
            Interlocked.CompareExchange(ref _reconnectStartedTimestamp, Stopwatch.GetTimestamp(), 0);
        }
        else
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (elapsed > _configuration.MaxReconnectDuration)
            {
                if (sessionManager.TryForceResetIfStalled())
                {
                    _logger.LogWarning(
                        "SDK reconnection stalled (session={HasSession}, connected={IsConnected}, elapsed={Elapsed}s). " +
                        "Starting manual reconnection...",
                        currentSession is not null,
                        sessionIsConnected,
                        elapsed.TotalSeconds);

                    Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);
                    await ReconnectSessionAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Restarts the session when connection is lost.
    /// Reuses existing SessionManager (CreateSessionAsync disposes old session internally).
    /// Thread-safe: Only called from single-threaded ExecuteAsync loop.
    /// </summary>
    private async Task ReconnectSessionAsync(CancellationToken cancellationToken)
    {
        var sessionManager = _sessionManager;
        if (sessionManager is null)
        {
            return;
        }

        var propertyWriter = _propertyWriter;
        if (propertyWriter is null)
        {
            return;
        }

        ReconnectionMetrics.RecordAttemptStart();

        // Create a linked CTS so KillAsync can cancel this reconnection mid-flight.
        // Without this, KillAsync disposes the session while we're still using it,
        // leading to BadSessionNotActivated errors and 60s hangs.
        using var reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _reconnectCts = reconnectCts;

        // Prevent OnKeepAlive from triggering SDK reconnection during manual reconnection.
        // Without this, keep-alive on the newly created session can fire immediately,
        // triggering OnKeepAlive → BeginReconnect → OnReconnectComplete → AbandonCurrentSession,
        // which nullifies the session while we're still setting up subscriptions and loading state.
        sessionManager.SetReconnecting(true);

        try
        {
            var token = reconnectCts.Token;
            _logger.LogInformation("Restarting OPC UA session...");

            // Start collecting updates - any incoming subscription notifications will be buffered
            // until we complete the full state reload
            propertyWriter.StartBuffering();

            // Create new session (CreateSessionAsync disposes old session internally)
            var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);
            var session = await sessionManager.CreateSessionAsync(application, _configuration, token).ConfigureAwait(false);

            // Clear all read-after-write state - new session means old pending reads and registrations are invalid
            sessionManager.ReadAfterWriteManager?.ClearAll();

            _logger.LogInformation(
                "New OPC UA session created successfully (id={SessionId}, connected={Connected}).",
                session.SessionId,
                session.Connected);

            await _structureLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Recreate MonitoredItems from owned properties (avoids memory leak from holding SDK objects)
                var monitoredItems = CreateMonitoredItemsForReconnection();
                if (monitoredItems.Count > 0)
                {
                    await sessionManager.CreateSubscriptionsAsync(monitoredItems, session, token).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Subscriptions recreated successfully with {Count} monitored items.",
                        monitoredItems.Count);
                }
            }
            finally
            {
                _structureLock.Release();
            }

            await propertyWriter.LoadInitialStateAndResumeAsync(token).ConfigureAwait(false);

            ReconnectionMetrics.RecordSuccess();
            Volatile.Write(ref _lastError, null);
            _logger.LogInformation("Session restart complete (id={SessionId}).", session.SessionId);
        }
        catch (OperationCanceledException) when (reconnectCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            ReconnectionMetrics.RecordAbandoned();
            _logger.LogInformation("Reconnection cancelled by kill. Will retry on next health check.");

            // Clear the session so health check can trigger a fresh reconnection attempt
            await sessionManager.ClearSessionAsync(cancellationToken).ConfigureAwait(false);

            throw; // Re-throw to trigger retry in ExecuteAsync
        }
        catch (Exception ex)
        {
            ReconnectionMetrics.RecordFailure();
            Volatile.Write(ref _lastError, ex);
            _logger.LogError(ex, "Failed to restart session. Will retry on next health check.");

            // Clear the session so health check can trigger a new reconnection attempt
            await sessionManager.ClearSessionAsync(cancellationToken).ConfigureAwait(false);

            throw; // Re-throw to trigger retry in ExecuteAsync
        }
        finally
        {
            // Always clear the reconnecting flag when manual reconnection completes.
            // On failure paths, ClearSessionAsync already resets this, but the explicit
            // reset ensures it's always cleared (especially on the success path).
            sessionManager.SetReconnecting(false);
            _reconnectCts = null;
        }
    }

    internal async Task<ReferenceDescription?> TryGetRootNodeAsync(ISession session, CancellationToken cancellationToken)
    {
        if (_configuration.RootPath is { Length: > 0 } rootPath)
        {
            var currentNodeId = ObjectIds.ObjectsFolder;

            for (var i = 0; i < rootPath.Length; i++)
            {
                var references = await BrowseNodeAsync(session, currentNodeId, cancellationToken).ConfigureAwait(false);
                var match = FindChildByBrowseName(references, rootPath[i]);
                if (match is null)
                {
                    return null;
                }

                if (i == rootPath.Length - 1)
                {
                    return match;
                }

                // ToNodeId returns null when the matched reference carries a namespace URI that
                // is not registered in the session's NamespaceTable. Return null so the caller
                // logs "could not find root node" and retries, instead of browsing a null NodeId
                // on the next iteration (which throws ArgumentNullException deep in the browse
                // primitive). Symmetric with the null-BrowseName tolerance in FindChildByBrowseName.
                var resolvedNodeId = ExpandedNodeId.ToNodeId(match.NodeId, session.NamespaceUris);
                if (resolvedNodeId is null)
                {
                    _logger.LogWarning(
                        "Root path segment '{Segment}' resolved to ExpandedNodeId '{NodeId}' whose namespace URI is not registered in the session's NamespaceTable; cannot continue resolving the root node.",
                        rootPath[i], match.NodeId);
                    return null;
                }

                currentNodeId = resolvedNodeId;
            }
        }

        return new ReferenceDescription
        {
            NodeId = new ExpandedNodeId(ObjectIds.ObjectsFolder),
            BrowseName = new QualifiedName("Objects", 0)
        };
    }

    public override async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (_writer is null)
        {
            return WriteResult.Failure(changes, new InvalidOperationException("OPC UA client not started."));
        }

        return await _writer.WriteChangesAsync(changes, cancellationToken).ConfigureAwait(false);
    }

    internal void OnCurrentSessionChanged(ISession? previousSession, ISession? currentSession)
    {
        var handler = CurrentSessionChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new OpcUaCurrentSessionChangedEventArgs(previousSession, currentSession));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPC UA CurrentSessionChanged event handler threw an exception.");
        }
    }

    /// <inheritdoc />
    async Task IFaultInjectable.InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken)
    {
        switch (faultType)
        {
            case FaultType.Kill:
                await KillSessionAsync().ConfigureAwait(false);
                break;
            case FaultType.Disconnect:
                await DisconnectTransportAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(faultType), faultType, null);
        }
    }

    private async Task KillSessionAsync()
    {
        var sessionManager = _sessionManager;
        if (sessionManager != null)
        {
            _logger.LogWarning("Chaos: killing OPC UA client session.");

            var reconnectCts = _reconnectCts;
            if (reconnectCts is not null)
            {
                // Manual reconnection in progress — just cancel it.
                // ReconnectSessionAsync's catch block will handle cleanup via ClearSessionAsync.
                // Calling ClearSessionAsync here would race: it disposes the session that
                // ReconnectSessionAsync just created, causing BadSessionNotActivated on the
                // next read and triggering a permanent failure loop.
                try { await reconnectCts.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { /* CTS disposed between check and cancel */ }
            }
            else
            {
                // No manual reconnection in progress — clear session directly.
                await sessionManager.ClearSessionAsync(CancellationToken.None).ConfigureAwait(false);

                // If ReconnectSessionAsync started between our _reconnectCts check and ClearSessionAsync,
                // cancel it to speed up recovery instead of waiting for it to fail naturally.
                var lateCts = _reconnectCts;
                if (lateCts is not null)
                {
                    try { await lateCts.CancelAsync().ConfigureAwait(false); }
                    catch (ObjectDisposedException) { /* CTS disposed between check and cancel */ }
                }
            }
        }
    }

    private async Task DisconnectTransportAsync(CancellationToken cancellationToken)
    {
        var sessionManager = _sessionManager;
        if (sessionManager != null)
        {
            if (sessionManager.IsReconnecting)
            {
                // Reconnection in progress — transport disconnect would destroy the session
                // being set up, causing the reconnection to fail. Skip is safe because the
                // reconnection itself will establish a fresh connection.
                _logger.LogDebug("Chaos: skipping disconnect during active reconnection.");
                return;
            }

            _logger.LogWarning("Chaos: disconnecting OPC UA client transport.");

            // Note: TOCTOU — reconnection could start between the IsReconnecting check and
            // DisconnectTransportAsync(). This is benign: the transport close triggers keep-alive
            // failure on the new session, which is caught and retried by the health check loop.
            await sessionManager.DisconnectTransportAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ReferenceDescriptionCollection> BrowseNodeAsync(
        ISession session,
        NodeId nodeId,
        CancellationToken cancellationToken)
    {
        var results = await session.BrowseNodesAsync(
            [nodeId],
            _configuration.MaxReferencesPerNode,
            _configuration.MaxBrowseContinuations,
            _logger,
            cancellationToken).ConfigureAwait(false);

        return results.TryGetValue(nodeId, out var refs) ? refs : new ReferenceDescriptionCollection();
    }

    internal static ReferenceDescription? FindChildByBrowseName(ReferenceDescriptionCollection references, string browseName)
    {
        // These raw references bypass DistinctByResolvedNodeId, so BrowseName may be null.
        return references.FirstOrDefault(reference => reference.BrowseName?.Name == browseName);
    }

    private void Reset()
    {
        _isStarted = false;
        CleanupPropertyData();
    }

    private void RemoveItemsForSubject(IInterceptorSubject subject)
    {
        _sessionManager?.SubscriptionManager.RemoveItemsForSubject(subject);
        _sessionManager?.PollingManager?.RemoveItemsForSubject(subject);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // Already disposed
        }

        // DisposeAsync can run without a completed StopAsync (e.g. HomeBlaze disposes in a
        // finally block when detach fails or times out). An in-flight initial load or
        // reconnect holds _structureLock across network I/O, so cancel both first;
        // otherwise the uncancellable wait below stalls disposal until they finish naturally.
        var loadCts = _loadCts;
        if (loadCts is not null)
        {
            try { await loadCts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* CTS disposed between check and cancel */ }
        }

        var reconnectCts = _reconnectCts;
        if (reconnectCts is not null)
        {
            try { await reconnectCts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* CTS disposed between check and cancel */ }
        }

        await _structureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var sessionManager = _sessionManager;
            if (sessionManager is not null)
            {
                await sessionManager.DisposeAsync().ConfigureAwait(false);
                _sessionManager = null;
            }
        }
        finally
        {
            _structureLock.Release();
        }

        // Clean up property data to prevent memory leaks
        // This ensures that property data associated with this OpcUaNodeIdKey is cleared
        // even if properties are reused across multiple source instances
        CleanupPropertyData();
        _ownership.Dispose();
        Dispose();
    }

    private void CleanupPropertyData()
    {
        foreach (var property in _ownership.Properties)
        {
            property.RemovePropertyData(OpcUaNodeIdKey);
        }
    }
}
