using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubjectClientSource : BackgroundService, ISubjectSource
{
    private const string OpcVariableKey = "OpcVariable";
    private const int DefaultChunkSize = 512;

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly List<PropertyReference> _propertiesWithOpcData = [];

    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly OpcUaSubscriptionManager _subscriptionManager;
    private readonly OpcUaSessionManager _sessionManager;
    private readonly OpcUaWriteQueueManager _writeQueueManager;
    private readonly SemaphoreSlim _writeFlushSemaphore = new(1, 1);
    private CancellationToken _stoppingToken;
    private int _disposed = 0; // 0 = not disposed, 1 = disposed

    /// <summary>
    /// Gets the number of write operations currently queued due to disconnection.
    /// Used for monitoring and diagnostics.
    /// </summary>
    public int PendingWriteCount => _writeQueueManager.PendingWriteCount;

    /// <summary>
    /// Gets the number of writes that were dropped due to ring buffer overflow since the last reconnect.
    /// This counter is reset to zero when the pending writes are successfully flushed after reconnection.
    /// Used for monitoring and diagnostics.
    /// </summary>
    public int DroppedWriteCount => _writeQueueManager.DroppedWriteCount;

    /// <summary>
    /// Gets a value indicating whether the client is currently connected to the OPC UA server.
    /// Returns true when a session is established, false otherwise.
    /// </summary>
    public bool IsConnected => _sessionManager.IsConnected;

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
        _sessionManager = new OpcUaSessionManager(logger);
        _writeQueueManager = new OpcUaWriteQueueManager(_configuration.WriteQueueSize, logger);

        // Wire up session change events
        _sessionManager.SessionChanged += OnSessionChanged;
        _sessionManager.ReconnectionCompleted += OnReconnectionCompleted;
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
        _stoppingToken = stoppingToken;

        // Initial connection retry loop - needed because SessionReconnectHandler only works after initial connect
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Connecting to OPC UA server at {ServerUrl}...", _configuration.ServerUrl);

                var application = _configuration.CreateApplicationInstance();
                var newSession = await _sessionManager.CreateSessionAsync(application, _configuration, stoppingToken);

                _subscriptionManager.Clear();
                _propertiesWithOpcData.Clear();

                _logger.LogInformation("Connected to OPC UA server successfully.");

                var rootNode = await TryGetRootNodeAsync(newSession, stoppingToken);
                if (rootNode is not null)
                {
                    var monitoredItems = await _subjectLoader.LoadSubjectAsync(_subject, rootNode, newSession, stoppingToken);
                    if (monitoredItems.Count > 0)
                    {
                        await ReadAndApplyInitialValuesAsync(monitoredItems, newSession, stoppingToken);
                        await _subscriptionManager.CreateBatchedSubscriptionsAsync(monitoredItems, newSession, stoppingToken);
                        _subscriptionManager.StartHealthMonitoring();
                    }

                    _logger.LogInformation("OPC UA client initialization complete. Monitoring {Count} items.", monitoredItems.Count);
                }

                // Wait for stop request (SessionManager handles all reconnection automatically)
                await Task.Delay(Timeout.Infinite, stoppingToken);

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
                await _sessionManager.CloseSessionAsync();
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
        await _sessionManager.CloseSessionAsync();

        _logger.LogInformation("OPC UA client has stopped.");
    }

    /// <summary>
    /// Handles session changes from the OpcUaSessionManager.
    /// </summary>
    private void OnSessionChanged(object? sender, SessionChangedEventArgs e)
    {
        if (e is { IsNewSession: true, Session: not null })
        {
            _logger.LogInformation("New session established. Subscriptions have been transferred by OPC UA stack.");

            // SessionReconnectHandler automatically transfers subscriptions to the new session
            _subscriptionManager.UpdateTransferredSubscriptions(e.Session.Subscriptions);
            _logger.LogInformation("Session transfer complete: Subscriptions preserved with all monitored items intact.");
        }
        else if (e.Session == null)
        {
            _logger.LogWarning("Session disconnected permanently.");
        }
    }

    /// <summary>
    /// Handles reconnection completion from the OpcUaSessionManager.
    /// Flushes pending writes, blocking new writes until complete.
    /// </summary>
    private async void OnReconnectionCompleted(object? sender, EventArgs e)
    {
        // Check if disposed
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
        {
            return;
        }

        if (_writeQueueManager.IsEmpty)
        {
            return;
        }

        try
        {
            // Wait for semaphore - this BLOCKS new writes until flush completes
            // This ensures queued writes are sent before any new writes
            await _writeFlushSemaphore.WaitAsync(_stoppingToken);
            try
            {
                var result = await _sessionManager.ExecuteWithSessionAsync(
                    async session =>
                    {
                        await FlushPendingWritesAsync(session, _stoppingToken);
                        return true;
                    },
                    _stoppingToken);

                if (result)
                {
                    _logger.LogInformation("Successfully flushed pending writes after reconnection.");
                }
            }
            finally
            {
                _writeFlushSemaphore.Release();
            }
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Write flush cancelled during shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush pending writes after reconnection");
        }
    }

    private async Task<ReferenceDescription?> TryGetRootNodeAsync(Session session, CancellationToken cancellationToken)
    {
        if (_configuration.RootName is not null)
        {
            foreach (var reference in await BrowseNodeAsync(session, ObjectIds.ObjectsFolder, cancellationToken))
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

    private async Task ReadAndApplyInitialValuesAsync(IReadOnlyList<MonitoredItem> monitoredItems, Session session, CancellationToken cancellationToken)
    {
        var itemCount = monitoredItems.Count;
        if (itemCount == 0)
        {
            return;
        }

        try
        {
            var result = new Dictionary<RegisteredSubjectProperty, DataValue>();

            var chunkSize = (int)(session.OperationLimits?.MaxNodesPerRead ?? DefaultChunkSize);
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

                var readResponse = await session.ReadAsync(null, 0, TimestampsToReturn.Source, readValues, cancellationToken);
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
    /// Thread-safe with session manager for session access during reconnection.
    /// Uses semaphore to prevent race between queue flush and new writes.
    /// </summary>
    public async ValueTask WriteToSourceAsync(IReadOnlyList<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }

        // Acquire semaphore to ensure atomic flush-then-write operation
        await _writeFlushSemaphore.WaitAsync(cancellationToken);
        try
        {
            var result = await _sessionManager.ExecuteWithSessionAsync(
                async session =>
                {
                    // Flush queue first (if any), then write new changes
                    // This ensures FIFO ordering
                    if (!_writeQueueManager.IsEmpty)
                    {
                        await FlushPendingWritesAsync(session, cancellationToken);
                    }

                    await WriteToSourceAsync(changes, session, cancellationToken);
                    return true;
                },
                cancellationToken);

            // If no session, queue the changes
            if (result != true)
            {
                _writeQueueManager.EnqueueBatch(changes);
            }
        }
        finally
        {
            _writeFlushSemaphore.Release();
        }
    }

    private async Task WriteToSourceAsync(IReadOnlyList<SubjectPropertyChange> changes, Session session, CancellationToken cancellationToken)
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
        var pendingWrites = _writeQueueManager.DequeueAll();
        if (pendingWrites.Count > 0)
        {
            _logger.LogInformation("Flushing {Count} queued writes to server", pendingWrites.Count);
            await WriteToSourceAsync(pendingWrites, session, cancellationToken);
        }
    }

    private async Task<ReferenceDescriptionCollection> BrowseNodeAsync(
        Session session,
        NodeId nodeId,
        CancellationToken cancellationToken)
    {
        const uint nodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

        var (_, _, nodeProperties, _) = await session.BrowseAsync(
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
        if (_writeQueueManager.IsEmpty)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Attempting to flush {Count} pending writes before disconnect...", _writeQueueManager.PendingWriteCount);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            var result = await _sessionManager.ExecuteWithSessionAsync(
                async session =>
                {
                    await FlushPendingWritesAsync(session, cts.Token);
                    return true;
                },
                cts.Token);

            if (result == true)
            {
                _logger.LogInformation("Successfully flushed pending writes before disconnect.");
            }
            else
            {
                _logger.LogWarning("Dropping {Count} pending writes - session already closed.", _writeQueueManager.PendingWriteCount);
                _writeQueueManager.Clear();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush {Count} pending writes before disconnect. Writes will be dropped.", _writeQueueManager.PendingWriteCount);
            _writeQueueManager.Clear();
        }
    }

    public override void Dispose()
    {
        _sessionManager.SessionChanged -= OnSessionChanged;
        _sessionManager.ReconnectionCompleted -= OnReconnectionCompleted;

        _subscriptionManager.Dispose();
        _sessionManager.Dispose();
        _writeFlushSemaphore.Dispose();
        base.Dispose();
    }
}
