using System.Diagnostics;
using Microsoft.Extensions.Hosting;
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

internal sealed class OpcUaSubjectClientSource : BackgroundService, ISubjectSource, ISubjectConnector, IFaultInjectable, IAsyncDisposable
{
    private const int DefaultChunkSize = 512;

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;

    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly SubscriptionHealthMonitor _subscriptionHealthMonitor;

    private volatile SessionManager? _sessionManager;
    private SubjectPropertyWriter? _propertyWriter;

    private readonly SemaphoreSlim _structureLock = new(1, 1);
    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)
    private volatile bool _isStarted;
    private long _reconnectStartedTimestamp; // 0 = not reconnecting, otherwise Stopwatch timestamp when reconnection started (for stall detection)
    private volatile CancellationTokenSource? _reconnectCts; // Cancelled by KillAsync to abort in-flight reconnection

    // Diagnostics tracking - accessed from multiple threads via Diagnostics property
    private long _totalReconnectionAttempts;
    private long _successfulReconnections;
    private long _failedReconnections;
    private long _lastConnectedAtTicks; // 0 = never connected, otherwise UTC ticks (thread-safe via Interlocked)
    private OpcUaClientDiagnostics? _diagnostics;

    internal string OpcUaNodeIdKey { get; } = "OpcUaNodeId:" + Guid.NewGuid();

    /// <summary>
    /// Gets diagnostic information about the client connection state.
    /// </summary>
    public OpcUaClientDiagnostics Diagnostics => _diagnostics ??= new OpcUaClientDiagnostics(this);

    /// <summary>
    /// Gets the session manager for internal diagnostics access.
    /// </summary>
    internal SessionManager? SessionManager => _sessionManager;

    internal SourceOwnershipManager Ownership => _ownership;

    // Diagnostics accessors
    internal long TotalReconnectionAttempts => Interlocked.Read(ref _totalReconnectionAttempts);
    internal long SuccessfulReconnections => Interlocked.Read(ref _successfulReconnections);
    internal long FailedReconnections => Interlocked.Read(ref _failedReconnections);
    internal DateTimeOffset? LastConnectedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastConnectedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Called by SessionManager when a reconnection attempt starts (via SDK's SessionReconnectHandler).
    /// Updates diagnostics metrics to track the reconnection attempt.
    /// </summary>
    internal void RecordReconnectionAttemptStart()
    {
        Interlocked.Increment(ref _totalReconnectionAttempts);
    }

    /// <summary>
    /// Called by SessionManager when SDK's SessionReconnectHandler successfully reconnects.
    /// Updates diagnostics metrics to track the successful reconnection.
    /// Note: Does not increment TotalReconnectionAttempts - that's done in RecordReconnectionAttemptStart.
    /// </summary>
    internal void RecordReconnectionSuccess()
    {
        Interlocked.Increment(ref _successfulReconnections);
        Interlocked.Exchange(ref _lastConnectedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    private void RemoveItemsForSubject(IInterceptorSubject subject)
    {
        _structureLock.Wait();
        try
        {
            _sessionManager?.SubscriptionManager.RemoveItemsForSubject(subject);
            _sessionManager?.PollingManager?.RemoveItemsForSubject(subject);
        }
        finally
        {
            _structureLock.Release();
        }
    }

    public OpcUaSubjectClientSource(IInterceptorSubject subject, OpcUaClientConfiguration configuration, ILogger logger)
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
        _subjectLoader = new OpcUaSubjectLoader(configuration, _ownership, this, logger);
        _subscriptionHealthMonitor = new SubscriptionHealthMonitor(logger);
    }

    private void OnSubjectDetaching(IInterceptorSubject subject)
    {
        RemoveItemsForSubject(subject);
    }

    /// <inheritdoc />
    public IInterceptorSubject RootSubject => _subject;

    public async Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        Reset();

        _propertyWriter = propertyWriter;
        _logger.LogInformation("Connecting to OPC UA server at {ServerUrl}.", _configuration.ServerUrl);

        _sessionManager = new SessionManager(this, propertyWriter, _configuration, _logger);

        var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);
        var session = await _sessionManager.CreateSessionAsync(application, _configuration, cancellationToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _lastConnectedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
        _logger.LogInformation("Connected to OPC UA server successfully.");

        await _structureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rootNode = await TryGetRootNodeAsync(session, cancellationToken).ConfigureAwait(false);
            if (rootNode is not null)
            {
                var monitoredItems = await _subjectLoader.LoadSubjectAsync(_subject, rootNode, session, cancellationToken).ConfigureAwait(false);
                if (monitoredItems.Count > 0)
                {
                    await _sessionManager.CreateSubscriptionsAsync(monitoredItems, session, cancellationToken).ConfigureAwait(false);
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
        }
        finally
        {
            _structureLock.Release();
        }

        _isStarted = true;
        return _sessionManager;
    }

    public int WriteBatchSize => (int)(_sessionManager?.CurrentSession?.OperationLimits?.MaxNodesPerWrite ?? 0);

    public async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
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
        var batchSize = (int)(session.OperationLimits?.MaxNodesPerRead ?? DefaultChunkSize);
        batchSize = batchSize is 0 ? int.MaxValue : batchSize;

        var result = new Dictionary<RegisteredSubjectProperty, DataValue>(itemCount);
        for (var offset = 0; offset < itemCount; offset += batchSize)
        {
            var take = Math.Min(batchSize, itemCount - offset);
            var readValues = new ReadValueIdCollection(take);

            for (var i = 0; i < take; i++)
            {
                readValues.Add(new ReadValueId
                {
                    NodeId = ownedProperties[offset + i].NodeId,
                    AttributeId = Opc.Ua.Attributes.Value
                });
            }

            var readResponse = await session.ReadAsync(
                requestHeader: null,
                maxAge: 0,
                timestampsToReturn: TimestampsToReturn.Source,
                readValues,
                cancellationToken).ConfigureAwait(false);

            var resultCount = Math.Min(readResponse.Results.Count, readValues.Count);
            for (var i = 0; i < resultCount; i++)
            {
                if (StatusCode.IsGood(readResponse.Results[i].StatusCode))
                {
                    var dataValue = readResponse.Results[i];
                    result[ownedProperties[offset + i].Property] = dataValue;
                }
            }
        }

        _logger.LogInformation("Successfully read {Count} OPC UA nodes from server.", itemCount);
        return () =>
        {
            foreach (var (property, dataValue) in result)
            {
                var value = _configuration.ValueConverter.ConvertToPropertyValue(dataValue.Value, property);
                property.SetValueFromSource(this, dataValue.SourceTimestamp, null, value);
            }

            _logger.LogInformation("Updated {Count} properties with OPC UA node values.", itemCount);
        };
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
            var monitoredItem = MonitoredItemFactory.Create(_configuration, nodeId, property);
            monitoredItems.Add(monitoredItem);
        }

        return monitoredItems;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Single-threaded health check loop. Coordinates with automatic reconnection via IsReconnecting flag.
        // All async work from SDK reconnection callbacks is deferred to this loop for simplicity.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sessionManager = _sessionManager; // Capture reference to avoid TOCTOU
                var propertyWriter = _propertyWriter;
                if (sessionManager is not null && propertyWriter is not null && _isStarted)
                {
                    // 1. Cleanup sessions queued for disposal from SDK reconnection
                    if (sessionManager.HasSessionsToDispose)
                    {
                        await sessionManager.DisposePendingSessionsAsync(stoppingToken).ConfigureAwait(false);
                    }

                    // 2. Check session health and trigger reconnection if needed
                    var isReconnecting = sessionManager.IsReconnecting;
                    var currentSession = sessionManager.CurrentSession;
                    var sessionIsConnected = currentSession?.Connected ?? false;

                    if (currentSession is not null && sessionIsConnected && !isReconnecting)
                    {
                        // Session healthy - reset stall detection timestamp
                        Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);

                        // After SDK reconnection with subscription transfer, perform a full state read
                        // to ensure eventual consistency. Subscription notifications alone may be incomplete
                        // (notification queue overflow, timing gaps, subscription lifetime expiration).
                        await sessionManager.PerformFullStateSyncIfNeededAsync(stoppingToken).ConfigureAwait(false);

                        // Check if any subscription has stopped publishing (server stopped sending responses).
                        // This can happen when another client's session disruption affects the server's publish pipeline.
                        // The session appears connected but notifications aren't flowing — trigger manual reconnection.
                        if (sessionManager.SubscriptionManager.HasStoppedPublishing)
                        {
                            _logger.LogWarning(
                                "OPC UA subscription has stopped publishing. Starting manual reconnection to recover notification flow...");
                            await ReconnectSessionAsync(stoppingToken).ConfigureAwait(false);
                            continue;
                        }

                        await _subscriptionHealthMonitor.CheckAndHealSubscriptionsAsync(
                            sessionManager.Subscriptions,
                            stoppingToken).ConfigureAwait(false);
                    }
                    else if (!isReconnecting && (currentSession is null || !sessionIsConnected))
                    {
                        // Session is dead and no reconnection in progress
                        Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);
                        _logger.LogWarning(
                            "OPC UA session is dead (session={HasSession}, connected={IsConnected}). " +
                            "Starting manual reconnection...",
                            currentSession is not null,
                            sessionIsConnected);

                        await ReconnectSessionAsync(stoppingToken).ConfigureAwait(false);
                    }
                    else if (isReconnecting)
                    {
                        // SDK reconnection in progress - check for stall using time-based detection
                        // Note: We check stall regardless of session.Connected state because the old
                        // session's Connected property can return stale values during SDK reconnection.
                        // Uses Stopwatch for monotonic timing (immune to clock drift/jumps).
                        var startedAt = Interlocked.Read(ref _reconnectStartedTimestamp);
                        if (startedAt == 0)
                        {
                            // First detection of reconnecting state - record start time
                            Interlocked.CompareExchange(ref _reconnectStartedTimestamp, Stopwatch.GetTimestamp(), 0);
                        }
                        else
                        {
                            var elapsed = Stopwatch.GetElapsedTime(startedAt);
                            if (elapsed > _configuration.MaxReconnectDuration)
                            {
                                // SDK handler likely timed out or is stuck - force reset and trigger manual reconnection
                                if (sessionManager.TryForceResetIfStalled())
                                {
                                    _logger.LogWarning(
                                        "SDK reconnection stalled (session={HasSession}, connected={IsConnected}, elapsed={Elapsed}s). " +
                                        "Starting manual reconnection...",
                                        currentSession is not null,
                                        sessionIsConnected,
                                        elapsed.TotalSeconds);

                                    Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);
                                    await ReconnectSessionAsync(stoppingToken).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                }

                await Task.Delay(_configuration.SubscriptionHealthCheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check or session restart. Retrying after delay.");
                await Task.Delay(_configuration.SubscriptionHealthCheckInterval, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("OPC UA client has stopped.");
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

        Interlocked.Increment(ref _totalReconnectionAttempts);

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

            RecordReconnectionSuccess();
            _logger.LogInformation("Session restart complete (id={SessionId}).", session.SessionId);
        }
        catch (OperationCanceledException) when (reconnectCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Reconnection was cancelled by KillAsync, not by shutdown.
            // Don't increment failed counter - this is expected during chaos testing.
            _logger.LogInformation("Reconnection cancelled by kill. Will retry on next health check.");

            // Clear the session so health check can trigger a fresh reconnection attempt
            await sessionManager.ClearSessionAsync(cancellationToken).ConfigureAwait(false);

            throw; // Re-throw to trigger retry in ExecuteAsync
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedReconnections);
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

    private async Task<ReferenceDescription?> TryGetRootNodeAsync(Session session, CancellationToken cancellationToken)
    {
        if (_configuration.RootName is not null)
        {
            var references = await BrowseNodeAsync(session, ObjectIds.ObjectsFolder, cancellationToken).ConfigureAwait(false);
            return references.FirstOrDefault(reference => reference.BrowseName.Name == _configuration.RootName);
        }

        return new ReferenceDescription
        {
            NodeId = new ExpandedNodeId(ObjectIds.ObjectsFolder),
            BrowseName = new QualifiedName("Objects", 0)
        };
    }

    /// <summary>
    /// Writes changes to OPC UA server with per-node result tracking.
    /// Returns a <see cref="WriteResult"/> indicating which nodes failed.
    /// Zero-allocation on success path.
    /// </summary>
    public async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        try
        {
            var session = _sessionManager?.CurrentSession;
            if (session is null || !session.Connected)
            {
                return WriteResult.Failure(changes, new InvalidOperationException("OPC UA session is not connected."));
            }

            var writeValues = CreateWriteValuesCollection(changes);
            if (writeValues.Count is 0)
            {
                return WriteResult.Success;
            }

            var writeResponse = await session.WriteAsync(requestHeader: null, writeValues, cancellationToken).ConfigureAwait(false);
            var result = ProcessWriteResults(writeResponse.Results, changes);
            if (result.IsFullySuccessful || result.IsPartialFailure)
            {
                NotifyPropertiesWritten(changes);
            }

            return result;
        }
        catch (Exception ex)
        {
            return WriteResult.Failure(changes, ex);
        }
    }

    /// <summary>
    /// Processes write results. Zero-allocation on success path.
    /// </summary>
    private WriteResult ProcessWriteResults(StatusCodeCollection results, ReadOnlyMemory<SubjectPropertyChange> allChanges)
    {
        // Fast path: check if all succeeded (common case)
        var failureCount = 0;
        for (var i = 0; i < results.Count; i++)
        {
            if (!StatusCode.IsGood(results[i]))
            {
                failureCount++;
            }
        }

        if (failureCount == 0)
        {
            return WriteResult.Success;
        }

        // Failure case: re-scan to collect failed changes
        var failedChanges = new List<SubjectPropertyChange>(failureCount);
        var transientCount = 0;
        var resultIndex = 0;
        var span = allChanges.Span;
        for (var i = 0; i < span.Length && resultIndex < results.Count; i++)
        {
            var change = span[i];
            if (!TryGetWritableNodeId(change, out _, out _))
                continue;

            if (!StatusCode.IsGood(results[resultIndex]))
            {
                failedChanges.Add(change);
                if (IsTransientWriteError(results[resultIndex]))
                    transientCount++;
            }
            resultIndex++;
        }

        var successCount = results.Count - failedChanges.Count;
        var permanentCount = failedChanges.Count - transientCount;

        _logger.LogWarning(
            "OPC UA write batch partial failure: {SuccessCount} succeeded, {TransientCount} transient, {PermanentCount} permanent out of {TotalCount}.",
            successCount, transientCount, permanentCount, results.Count);

        var error = new OpcUaWriteException(transientCount, permanentCount, results.Count);
        return successCount > 0
            ? WriteResult.PartialFailure(failedChanges.ToArray(), error)
            : WriteResult.Failure(failedChanges.ToArray(), error);
    }

    private bool TryGetWritableNodeId(SubjectPropertyChange change, out NodeId nodeId, out RegisteredSubjectProperty registeredProperty)
    {
        nodeId = null!;
        registeredProperty = null!;

        if (!change.Property.TryGetPropertyData(OpcUaNodeIdKey, out var value) || value is not NodeId id)
        {
            return false;
        }

        if (change.Property.TryGetRegisteredProperty() is not { HasSetter: true } property)
        {
            return false;
        }

        nodeId = id;
        registeredProperty = property;
        return true;
    }

    private WriteValueCollection CreateWriteValuesCollection(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var span = changes.Span;
        var writeValues = new WriteValueCollection(span.Length);

        for (var i = 0; i < span.Length; i++)
        {
            var change = span[i];

            if (!TryGetWritableNodeId(change, out var nodeId, out var registeredProperty))
            {
                continue;
            }

            var convertedValue = _configuration.ValueConverter.ConvertToNodeValue(
                change.GetNewValue<object?>(),
                registeredProperty);

            writeValues.Add(new WriteValue
            {
                NodeId = nodeId,
                AttributeId = Opc.Ua.Attributes.Value,
                Value = new DataValue
                {
                    Value = convertedValue,
                    StatusCode = StatusCodes.Good,
                    SourceTimestamp = change.ChangedTimestamp.UtcDateTime
                }
            });
        }

        return writeValues;
    }

    /// <summary>
    /// Notifies ReadAfterWriteManager that properties were written.
    /// Schedules read-after-writes for properties where SamplingInterval=0 was revised to non-zero.
    /// </summary>
    private void NotifyPropertiesWritten(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var manager = _sessionManager?.ReadAfterWriteManager;
        if (manager is null)
        {
            return;
        }

        var span = changes.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i].Property.TryGetPropertyData(OpcUaNodeIdKey, out var nodeIdObj) &&
                nodeIdObj is NodeId nodeId)
            {
                manager.OnPropertyWritten(nodeId);
            }
        }
    }

    /// <summary>
    /// Determines if a write failure status code represents a transient error that should be retried.
    /// Returns true for transient errors (connectivity, timeouts, server load), false for permanent errors.
    /// </summary>
    internal static bool IsTransientWriteError(StatusCode statusCode)
    {
        // Permanent design-time errors: Don't retry
        if (statusCode == StatusCodes.BadNodeIdUnknown ||
            statusCode == StatusCodes.BadAttributeIdInvalid ||
            statusCode == StatusCodes.BadTypeMismatch ||
            statusCode == StatusCodes.BadWriteNotSupported ||
            statusCode == StatusCodes.BadUserAccessDenied ||
            statusCode == StatusCodes.BadNotWritable)
        {
            return false;
        }

        // Transient connectivity/server errors: Retry
        return StatusCode.IsBad(statusCode);
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
        Session session,
        NodeId nodeId,
        CancellationToken cancellationToken)
    {
        const uint nodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

        var (_, _, nodeProperties, _) = await session.BrowseAsync(
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

    private void Reset()
    {
        _isStarted = false;
        CleanupPropertyData();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // Already disposed
        }

        var sessionManager = _sessionManager;
        if (sessionManager is not null)
        {
            await sessionManager.DisposeAsync().ConfigureAwait(false);
            _sessionManager = null;
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
