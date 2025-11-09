using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;
using System.Collections.Concurrent;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubjectClientSource : BackgroundService, ISubjectSource
{
    private const string OpcVariableKey = "OpcVariable";
    private const int DefaultChunkSize = 512;

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly List<PropertyReference> _propertiesWithOpcData = [];

    private volatile Session? _session;  // volatile for thread-safe reads without semaphore

    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly OpcUaSubscriptionManager _subscriptionManager;
    private readonly ConcurrentQueue<SubjectPropertyChange> _pendingWrites = new();
    private readonly SessionReconnectHandler _reconnectHandler;
    private readonly SemaphoreSlim _sessionSemaphore = new(1, 1);
    private readonly TaskCompletionSource _stopRequestedTcs = new();

    private int _droppedWriteCount = 0; // Access via Interlocked operations only
    private volatile bool _isReconnecting = false;  // volatile for thread-safe access from multiple threads

    /// <summary>
    /// Gets the number of write operations currently queued due to disconnection.
    /// Used for monitoring and diagnostics.
    /// </summary>
    public int PendingWriteCount => _pendingWrites.Count;

    /// <summary>
    /// Gets the number of writes that were dropped due to ring buffer overflow since the last reconnect.
    /// This counter is reset to zero when the pending writes are successfully flushed after reconnection.
    /// Used for monitoring and diagnostics.
    /// </summary>
    public int DroppedWriteCount => Interlocked.CompareExchange(ref _droppedWriteCount, 0, 0);

    /// <summary>
    /// Gets a value indicating whether the client is currently connected to the OPC UA server.
    /// Returns true when a session is established, false otherwise.
    /// </summary>
    public bool IsConnected => _session != null;

    /// <summary>
    /// Gets the list of active OPC UA subscriptions.
    /// </summary>
    public IReadOnlyList<Subscription> Subscriptions => _subscriptionManager.Subscriptions;

    /// <summary>
    /// Gets the total count of monitored items across all subscriptions.
    /// </summary>
    public int TotalMonitoredItemCount => _subscriptionManager.TotalMonitoredItemCount;

    public OpcUaSubjectClientSource(
        IInterceptorSubject subject,
        OpcUaClientConfiguration configuration,
        ILogger<OpcUaSubjectClientSource> logger)
    {
        configuration.Validate(); // Fail fast with clear error messages

        _subject = subject;
        _logger = logger;
        _configuration = configuration;
        _subjectLoader = new OpcUaSubjectLoader(configuration, _propertiesWithOpcData, this, logger);
        _subscriptionManager = new OpcUaSubscriptionManager(configuration, logger);

        // Initialize SessionReconnectHandler with exponential backoff (max 60s)
        // First parameter: false = preserve session when possible (don't always close)
        // Second parameter: 60000ms = max reconnect period for exponential backoff
        _reconnectHandler = new SessionReconnectHandler(false, 60000);
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return _configuration.SourcePathProvider.IsPropertyIncluded(property);
    }

    public Task<IDisposable?> StartListeningAsync(ISubjectUpdater updater, CancellationToken cancellationToken)
    {
        _subscriptionManager.SetUpdater(updater);
        return Task.FromResult<IDisposable?>(null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial connection retry loop - needed because SessionReconnectHandler only works after initial connect
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Connecting to OPC UA server at {ServerUrl}...", _configuration.ServerUrl);

                var application = _configuration.CreateApplicationInstance();
                await application.CheckApplicationInstanceCertificates(false);

                var endpointConfiguration = EndpointConfiguration.Create(application.ApplicationConfiguration);
                var endpointDescription = CoreClientUtils.SelectEndpoint(application.ApplicationConfiguration, _configuration.ServerUrl, false);
                var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

                Session? newSession;
                await _sessionSemaphore.WaitAsync(stoppingToken);
                try
                {
                    newSession = await Session.Create(
                        application.ApplicationConfiguration,
                        endpoint,
                        false,
                        application.ApplicationName,
                        60000,
                        new UserIdentity(),
                        null, stoppingToken);

                    _session = newSession;
                    _isReconnecting = false;
                }
                finally
                {
                    _sessionSemaphore.Release();
                }

                // Setup KeepAlive event handler for automatic reconnection
                // From this point on, SessionReconnectHandler manages all reconnection
                newSession.KeepAlive += OnKeepAlive;

                _subscriptionManager.Clear();
                _propertiesWithOpcData.Clear();

                _logger.LogInformation("Connected to OPC UA server successfully.");

                var rootNode = await TryGetRootNodeAsync(stoppingToken);
                if (rootNode is not null)
                {
                    var monitoredItems = await _subjectLoader.LoadSubjectAsync(_subject, rootNode, newSession, stoppingToken);
                    if (monitoredItems.Count > 0)
                    {
                        await ReadAndApplyInitialValuesAsync(monitoredItems, stoppingToken);
                        await _subscriptionManager.CreateBatchedSubscriptionsAsync(monitoredItems, newSession, stoppingToken);
                        _subscriptionManager.StartHealthMonitoring();
                    }

                    _logger.LogInformation("OPC UA client initialization complete. Monitoring {Count} items.", monitoredItems.Count);
                }

                // Wait for stop request (SessionReconnectHandler manages all reconnection automatically)
                await _stopRequestedTcs.Task.WaitAsync(stoppingToken);

                // If we reach here, stop was requested - exit the retry loop
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("OPC UA client is stopping.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to OPC UA server: {Message}. Will retry...", ex.Message);

                // Clean up any partial state before retry
                await _sessionSemaphore.WaitAsync(stoppingToken);
                try
                {
                    if (_session != null)
                    {
                        _session.KeepAlive -= OnKeepAlive;
                        _session = null;
                    }
                    _isReconnecting = false;
                }
                finally
                {
                    _sessionSemaphore.Release();
                }

                _subscriptionManager.Cleanup();
                CleanUpProperties();

                // Wait before retry (initial connection retry delay)
                if (!stoppingToken.IsCancellationRequested)
                {
                    var retryDelay = _configuration.ReconnectDelay;
                    _logger.LogInformation("Waiting {Delay} before retrying initial connection...", retryDelay);
                    await Task.Delay(retryDelay, stoppingToken);
                }
            }
        }

        // Final cleanup
        _subscriptionManager.Cleanup();
        CleanUpProperties();
        await TryFlushPendingWritesOnDisconnectAsync();
        await CloseSessionAsync();

        _logger.LogInformation("OPC UA client has stopped.");
    }

    /// <summary>
    /// Handles KeepAlive events and triggers automatic reconnection using SessionReconnectHandler
    /// when connection is lost. This provides exponential backoff and subscription preservation.
    /// </summary>
    private void OnKeepAlive(ISession sender, KeepAliveEventArgs e)
    {
        // Only handle bad status - good status means connection is healthy
        if (ServiceResult.IsGood(e.Status))
        {
            return;
        }

        _logger.LogWarning("KeepAlive failed with status: {Status}. Connection may be lost.", e.Status);

        // Critical connection states that require reconnection
        if (e.CurrentState == ServerState.Unknown || e.CurrentState == ServerState.Failed)
        {
            _sessionSemaphore.Wait();
            try
            {
                // Prevent duplicate reconnect attempts
                if (_isReconnecting)
                {
                    return;
                }

                var session = _session;
                if (session == null || !ReferenceEquals(sender, session))
                {
                    return;
                }

                // Check if SessionReconnectHandler is ready to begin reconnect
                if (_reconnectHandler.State != SessionReconnectHandler.ReconnectState.Ready)
                {
                    _logger.LogWarning("SessionReconnectHandler not ready. Current state: {State}", _reconnectHandler.State);
                    return;
                }

                _logger.LogInformation("Server connection lost. Beginning reconnect with exponential backoff...");

                // BeginReconnect returns the new state and triggers automatic reconnection
                // with exponential backoff (5s, 10s, 20s, 40s, 60s max)
                var reconnectPeriod = Math.Max(5000, _configuration.ReconnectDelay.TotalMilliseconds);
                var newState = _reconnectHandler.BeginReconnect(
                    session,
                    (int)reconnectPeriod,
                    OnReconnectComplete);

                if (newState == SessionReconnectHandler.ReconnectState.Triggered ||
                    newState == SessionReconnectHandler.ReconnectState.Reconnecting)
                {
                    _isReconnecting = true;
                    e.CancelKeepAlive = true; // Stop keep-alive during reconnect

                    _logger.LogInformation("Reconnect handler initiated successfully. Initial reconnect period: {Period}ms", reconnectPeriod);
                }
                else
                {
                    _logger.LogError("Failed to begin reconnect. Handler state: {State}", newState);
                }
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }
    }

    /// <summary>
    /// Callback invoked by SessionReconnectHandler when reconnection completes (success or failure).
    /// Handles session transfer and subscription preservation per OPC Foundation design.
    /// </summary>
    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        _sessionSemaphore.Wait();
        try
        {
            var reconnectedSession = _reconnectHandler.Session;

            if (reconnectedSession == null)
            {
                _logger.LogError("Reconnect completed but received null session. Connection lost permanently.");
                _isReconnecting = false;
                return;
            }

            var isNewSession = !ReferenceEquals(_session, reconnectedSession);

            if (isNewSession)
            {
                _logger.LogInformation("Reconnect created new session. Subscriptions have been transferred by OPC UA stack.");

                // SessionReconnectHandler automatically transfers subscriptions to the new session
                // EMBRACE this - the OPC UA stack has already done the work for us!
                var transferredSubscriptions = reconnectedSession.Subscriptions;
                var subscriptionCount = transferredSubscriptions.Count();
                _logger.LogInformation("Session transfer complete: {Count} subscriptions preserved with all monitored items intact.", subscriptionCount);

                // Update our subscription manager to reference the transferred subscriptions
                _subscriptionManager.UpdateTransferredSubscriptions(transferredSubscriptions);

                // Update session reference
                var oldSession = _session;
                _session = reconnectedSession as Session;

                // Clean up old session event handler
                if (oldSession != null)
                {
                    oldSession.KeepAlive -= OnKeepAlive;
                }

                // Setup KeepAlive for new session (only if it's a Session, not just ISession)
                if (_session != null)
                {
                    _session.KeepAlive += OnKeepAlive;
                }
            }
            else
            {
                _logger.LogInformation("Reconnect preserved existing session. Subscriptions maintained without transfer.");
            }

            _isReconnecting = false;

            _logger.LogInformation("Session reconnect completed successfully. New session: {IsNew}", isNewSession);

            // Flush pending writes now that reconnection is complete
            FlushPendingWritesAfterReconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling reconnect completion");
            _isReconnecting = false;
        }
        finally
        {
            _sessionSemaphore.Release();
        }
    }

    /// <summary>
    /// Flushes queued writes after successful reconnection.
    /// Runs asynchronously to avoid blocking the reconnect callback.
    /// </summary>
    private async Task FlushPendingWritesAfterReconnectAsync()
    {
        if (_pendingWrites.IsEmpty)
        {
            return;
        }

        try
        {
            await _sessionSemaphore.WaitAsync();
            try
            {
                var session = _session;
                if (session != null && !_isReconnecting)
                {
                    await FlushPendingWritesAsync(session, CancellationToken.None);
                    _logger.LogInformation("Successfully flushed pending writes after reconnection.");
                }
            }
            finally
            {
                _sessionSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush pending writes after reconnection.");
        }
    }

    private async Task<ReferenceDescription?> TryGetRootNodeAsync(CancellationToken cancellationToken)
    {
        if (_configuration.RootName is not null)
        {
            foreach (var reference in await BrowseNodeAsync(ObjectIds.ObjectsFolder, cancellationToken))
            {
                if (reference.BrowseName.Name == _configuration.RootName)
                {
                    return reference;
                }
            }

            return null;
        }

        return new ReferenceDescription
        {
            NodeId = new ExpandedNodeId(ObjectIds.ObjectsFolder),
            BrowseName = new QualifiedName("Objects", 0)
        };
    }

    private void CleanUpProperties()
    {
        foreach (var property in _propertiesWithOpcData)
        {
            try
            {
                property.SetPropertyData(OpcVariableKey, null);
            }
            catch { /* ignore cleanup exceptions */ }
        }

        _propertiesWithOpcData.Clear();
    }

    private async Task ReadAndApplyInitialValuesAsync(IReadOnlyList<MonitoredItem> monitoredItems, CancellationToken cancellationToken)
    {
        var itemCount = monitoredItems.Count;
        if (itemCount == 0 || _session is null)
        {
            return;
        }

        try
        {
            var result = new Dictionary<RegisteredSubjectProperty, DataValue>();

            var chunkSize = (int)(_session.OperationLimits?.MaxNodesPerRead ?? DefaultChunkSize);
            chunkSize = chunkSize == 0 ? int.MaxValue : chunkSize;

            for (var offset = 0; offset < itemCount; offset += chunkSize)
            {
                var take = Math.Min(chunkSize, itemCount - offset);
                var readValues = new ReadValueIdCollection(take);
                for (var i = 0; i < take; i++)
                {
                    readValues.Add(new ReadValueId
                    {
                        NodeId = monitoredItems[offset + i].StartNodeId,
                        AttributeId = Opc.Ua.Attributes.Value
                    });
                }

                var readResponse = await _session.ReadAsync(null, 0, TimestampsToReturn.Source, readValues, cancellationToken);
                var resultCount = Math.Min(readResponse.Results.Count, readValues.Count);
                for (var i = 0; i < resultCount; i++)
                {
                    if (StatusCode.IsGood(readResponse.Results[i].StatusCode))
                    {
                        var dataValue = readResponse.Results[i];
                        var monitoredItem = monitoredItems[offset + i];
                        if (monitoredItem.Handle is RegisteredSubjectProperty property)
                        {
                            result[property] = dataValue;
                        }
                    }
                }
            }
        
            foreach (var (property, dataValue) in result)
            {
                var value = _configuration.ValueConverter.ConvertToPropertyValue(dataValue.Value, property);
                property.SetValueFromSource(this, dataValue.SourceTimestamp, value);
            }

            _logger.LogInformation("Successfully read initial values of {Count} nodes.", itemCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read initial values for monitored items.");
            throw;
        }
    }

    public Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<Action?>(null);
    }

    /// <summary>
    /// Writes property changes to the OPC UA server. If disconnected, changes are queued using ring buffer semantics.
    /// Thread-safe with semaphore protection for session access during reconnection.
    /// </summary>
    public async ValueTask WriteToSourceAsync(IReadOnlyList<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }

        await _sessionSemaphore.WaitAsync(cancellationToken);
        try
        {
            var session = _session;
            if (session is not null && !_isReconnecting)
            {
                if (!_pendingWrites.IsEmpty)
                {
                    await FlushPendingWritesAsync(session, cancellationToken);
                }

                await WriteChangesToServerAsync(changes, session, cancellationToken);
            }
            else
            {
                if (_configuration.WriteQueueSize > 0)
                {
                    foreach (var change in changes)
                    {
                        // Ring buffer: remove oldest if at capacity
                        while (_pendingWrites.Count >= _configuration.WriteQueueSize)
                        {
                            if (_pendingWrites.TryDequeue(out _))
                            {
                                Interlocked.Increment(ref _droppedWriteCount);
                            }
                        }

                        _pendingWrites.Enqueue(change);
                    }

                    var dropped = Interlocked.CompareExchange(ref _droppedWriteCount, 0, 0);
                    if (dropped > 0)
                    {
                        _logger.LogWarning(
                            "Write queue at capacity, dropped {Count} oldest writes (queue size: {QueueSize}).",
                            dropped, _configuration.WriteQueueSize);
                    }
                }
                else
                {
                    _logger.LogWarning("Session is null and write buffering is disabled. Dropping {Count} writes.", changes.Count);
                }
            }
        }
        finally
        {
            _sessionSemaphore.Release();
        }
    }

    private async Task WriteChangesToServerAsync(IReadOnlyList<SubjectPropertyChange> changes, Session session, CancellationToken cancellationToken)
    {
        var count = changes.Count;
        if (count == 0)
        {
            return;
        }

        var chunkSize = (int)(session.OperationLimits?.MaxNodesPerWrite ?? DefaultChunkSize);
        chunkSize = chunkSize == 0 ? int.MaxValue : chunkSize;

        for (var offset = 0; offset < count; offset += chunkSize)
        {
            var take = Math.Min(chunkSize, count - offset);
            var writeValues = new WriteValueCollection(take);
            for (var i = 0; i < take; i++)
            {
                var change = changes[offset + i];
                if (change.Property.TryGetPropertyData(OpcVariableKey, out var v) && v is NodeId nodeId)
                {
                    var registeredProperty = change.Property.GetRegisteredProperty();
                    if (registeredProperty.HasSetter)
                    {
                        var value = _configuration.ValueConverter.ConvertToNodeValue(change.GetNewValue<object?>(), registeredProperty);
                        writeValues.Add(new WriteValue
                        {
                            NodeId = nodeId,
                            AttributeId = Opc.Ua.Attributes.Value,
                            Value = new DataValue
                            {
                                Value = value,
                                StatusCode = StatusCodes.Good,
                                SourceTimestamp = change.ChangedTimestamp.UtcDateTime
                            }
                        });
                    }
                }
            }

            if (writeValues.Count == 0)
            {
                continue;
            }

            var writeResponse = await session.WriteAsync(null, writeValues, cancellationToken);

            // Log individual write failures for better diagnostics
            for (var i = 0; i < Math.Min(writeResponse.Results.Count, writeValues.Count); i++)
            {
                if (StatusCode.IsBad(writeResponse.Results[i]))
                {
                    var change = changes[offset + i];
                    _logger.LogError(
                        "Failed to write {PropertyName} (NodeId: {NodeId}): {StatusCode}",
                        change.Property.Name,
                        writeValues[i].NodeId,
                        writeResponse.Results[i]);
                }
            }
        }
    }

    private async Task FlushPendingWritesAsync(Session session, CancellationToken cancellationToken)
    {
        var pendingWrites = new List<SubjectPropertyChange>();
        while (_pendingWrites.TryDequeue(out var change))
        {
            pendingWrites.Add(change);
        }

        if (pendingWrites.Count > 0)
        {
            _logger.LogInformation("Flushing {Count} queued writes to server", pendingWrites.Count);
            await WriteChangesToServerAsync(pendingWrites, session, cancellationToken);
            Interlocked.Exchange(ref _droppedWriteCount, 0);
        }
    }

    private async Task<ReferenceDescriptionCollection> BrowseNodeAsync(
        NodeId nodeId,
        CancellationToken cancellationToken)
    {
        const uint nodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

        var (_, _, nodeProperties, _) = await _session!.BrowseAsync(
            null,
            null,
            [nodeId],
            0u,
            BrowseDirection.Forward,
            ReferenceTypeIds.HierarchicalReferences,
            true,
            nodeClassMask,
            cancellationToken);

        return nodeProperties[0];
    }

    private async Task TryFlushPendingWritesOnDisconnectAsync()
    {
        if (_pendingWrites.Count == 0)
        {
            return;
        }

        var session = _session;
        if (session is null)
        {
            _logger.LogWarning("Dropping {Count} pending writes - session already closed.", _pendingWrites.Count);
            _pendingWrites.Clear();
            return;
        }

        try
        {
            _logger.LogInformation("Attempting to flush {Count} pending writes before disconnect...", _pendingWrites.Count);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await FlushPendingWritesAsync(session, cts.Token);
            _logger.LogInformation("Successfully flushed pending writes before disconnect.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush {Count} pending writes before disconnect. Writes will be dropped.", _pendingWrites.Count);
            _pendingWrites.Clear();
        }
    }

    private async Task CloseSessionAsync()
    {
        await _sessionSemaphore.WaitAsync();
        try
        {
            var session = _session;
            if (session is null)
            {
                return;
            }

            // Remove KeepAlive event handler before closing
            session.KeepAlive -= OnKeepAlive;

            try
            {
                // Use a short timeout CancellationToken instead of stoppingToken to avoid immediate cancellation
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await session.CloseAsync(cts.Token);
                }
                finally
                {
                    session.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while closing session.");
            }
            finally
            {
                // Null session AFTER disposal to prevent race conditions
                _session = null;
                _isReconnecting = false;
            }
        }
        finally
        {
            _sessionSemaphore.Release();
        }
    }

    public override void Dispose()
    {
        _subscriptionManager.Dispose();
        _reconnectHandler?.Dispose();
        _sessionSemaphore?.Dispose();
        base.Dispose();
    }
}
