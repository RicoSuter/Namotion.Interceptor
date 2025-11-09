using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal sealed class OpcUaSubjectClientSource : BackgroundService, ISubjectSource
{
    private const int DefaultChunkSize = 512;

    private readonly SemaphoreSlim _writeFlushSemaphore = new(1, 1);

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly List<PropertyReference> _propertiesWithOpcData = [];

    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly OpcUaSubscriptionManager _subscriptionManager;
    private readonly OpcUaSessionManager _sessionManager;
    private readonly OpcUaWriteQueueManager _writeQueueManager;
    private readonly OpcUaPollingManager? _pollingManager;

    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)
    private CancellationToken _stoppingToken;

    internal string OpcVariableKey { get; } = "OpcVariable:" + Guid.NewGuid();
    
    /// <summary>
    /// Gets a value indicating whether the source is currently connected to the OPC UA server.
    /// </summary>
    public bool IsConnected => _sessionManager.IsConnected;
    
    /// <summary>
    /// Gets the current subscriptions managed by the source.
    /// </summary>
    public IReadOnlyList<Subscription> Subscriptions => _subscriptionManager.Subscriptions;
    
    /// <summary>
    /// Gets the total count of monitored items across all subscriptions.
    /// </summary>
    public int TotalMonitoredItemCount => _subscriptionManager.TotalMonitoredItemCount;

    /// <summary>
    /// Gets the number of pending writes in the write queue.
    /// </summary>
    public int PendingWriteCount => _writeQueueManager.PendingWriteCount;
    
    /// <summary>
    /// Gets the total number of writes that have been dropped due to queue capacity limits.
    /// </summary>
    public int DroppedWriteCount => _writeQueueManager.DroppedWriteCount;

    /// <summary>
    /// Gets the number of items currently being polled (fallback for items that don't support subscriptions).
    /// Returns 0 if polling fallback is disabled.
    /// </summary>
    public int PollingItemCount => _pollingManager?.PollingItemCount ?? 0;

    public OpcUaSubjectClientSource(
        IInterceptorSubject subject,
        OpcUaClientConfiguration configuration,
        ILogger<OpcUaSubjectClientSource> logger)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        configuration.Validate();

        _subject = subject;
        _logger = logger;
        _configuration = configuration;

        _subjectLoader = new OpcUaSubjectLoader(configuration, _propertiesWithOpcData, this, logger);
        _sessionManager = new OpcUaSessionManager(logger);
        _writeQueueManager = new OpcUaWriteQueueManager(_configuration.WriteQueueSize, logger);

        // Create polling manager if enabled
        if (configuration.EnablePollingFallback)
        {
            _pollingManager = new OpcUaPollingManager(
                logger: logger,
                sessionManager: _sessionManager,
                pollingInterval: configuration.PollingInterval,
                batchSize: configuration.PollingBatchSize
            );
        }

        _subscriptionManager = new OpcUaSubscriptionManager(configuration, logger, _pollingManager);

        _sessionManager.SessionChanged += OnSessionChanged;
        _sessionManager.ReconnectionCompleted += OnReconnectionCompleted;
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property) =>
        _configuration.SourcePathProvider.IsPropertyIncluded(property);

    public Task<IDisposable?> StartListeningAsync(ISubjectUpdater updater, CancellationToken cancellationToken)
    {
        _subscriptionManager.SetUpdater(updater);
        _pollingManager?.SetUpdater(updater);
        return Task.FromResult<IDisposable?>(null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken; // Store for event handlers

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Connecting to OPC UA server at {ServerUrl}.", _configuration.ServerUrl);

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

                        // Start polling manager if enabled
                        _pollingManager?.Start();
                    }

                    _logger.LogInformation("OPC UA client initialization complete. Monitoring {Count} items (subscriptions: {Subscribed}, polling: {Polled}).",
                        monitoredItems.Count,
                        _subscriptionManager.TotalMonitoredItemCount,
                        PollingItemCount);
                }

                await Task.Delay(Timeout.Infinite, stoppingToken);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("OPC UA client is stopping...");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to OPC UA server: {Message}.", ex.Message);

                await _sessionManager.CloseSessionAsync();
                _subscriptionManager.Cleanup();
                CleanUpPropertyVariableData();

                if (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Waiting {Delay} before retrying OPC UA connection.", _configuration.ReconnectDelay);
                    await Task.Delay(_configuration.ReconnectDelay, stoppingToken);
                }
            }
        }

        _subscriptionManager.Cleanup();
        CleanUpPropertyVariableData();
        await _sessionManager.CloseSessionAsync();

        _logger.LogInformation("OPC UA client has stopped.");
    }

    private async Task<ReferenceDescription?> TryGetRootNodeAsync(Session session, CancellationToken cancellationToken)
    {
        if (_configuration.RootName is not null)
        {
            var references = await BrowseNodeAsync(session, ObjectIds.ObjectsFolder, cancellationToken);
            return references.FirstOrDefault(reference => reference.BrowseName.Name == _configuration.RootName);
        }

        return new ReferenceDescription
        {
            NodeId = new ExpandedNodeId(ObjectIds.ObjectsFolder),
            BrowseName = new QualifiedName("Objects", 0)
        };
    }


    private void OnSessionChanged(object? sender, SessionChangedEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
        {
            return;
        }

        if (e is { IsNewSession: true, Session: not null })
        {
            _subscriptionManager.UpdateTransferredSubscriptions(e.Session.Subscriptions);
            _logger.LogInformation("New session established. Subscriptions transferred by OPC UA stack. Subscriptions preserved with all monitored items intact.");
        }
        else if (e.Session is null)
        {
            _logger.LogWarning("OPC UA session disconnected permanently.");
        }
    }

    /// <summary>
    /// Handles reconnection completion. Flushes pending writes with proper cancellation support.
    /// Uses semaphore to coordinate with concurrent writes.
    /// </summary>
    private void OnReconnectionCompleted(object? sender, EventArgs e)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
        {
            return;
        }

        // Queue async work with continuation to handle exceptions
        FlushPendingWritesAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogError(t.Exception, "Failed to flush pending OPC UA writes after reconnection");
            }
        }, TaskScheduler.Default);
    }

    private async Task FlushPendingWritesAsync()
    {
        try
        {
            // Wait for semaphore with cancellation support
            await _writeFlushSemaphore.WaitAsync(_stoppingToken);
            try
            {
                if (_writeQueueManager.IsEmpty)
                {
                    return;
                }

                var session = _sessionManager.CurrentSession;
                if (session is null)
                {
                    return;
                }

                var pendingWrites = _writeQueueManager.DequeueAll();
                if (pendingWrites.Count > 0)
                {
                    await WriteToSourceAsync(pendingWrites, session, _stoppingToken);
                    _logger.LogInformation("Successfully flushed {Count} pending OPC UA writes after reconnection.", pendingWrites.Count);
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
    }

    private async Task ReadAndApplyInitialValuesAsync(
        IReadOnlyList<MonitoredItem> monitoredItems,
        Session session,
        CancellationToken cancellationToken)
    {
        var itemCount = monitoredItems.Count;
        if (itemCount is 0)
            return;

        try
        {
            var result = new Dictionary<RegisteredSubjectProperty, DataValue>();
            var chunkSize = (int)(session.OperationLimits?.MaxNodesPerRead ?? DefaultChunkSize);
            chunkSize = chunkSize is 0 ? int.MaxValue : chunkSize;

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

                var readResponse = await session.ReadAsync(
                    requestHeader: null,
                    maxAge: 0,
                    timestampsToReturn: TimestampsToReturn.Source,
                    readValues,
                    cancellationToken);

                var resultCount = Math.Min(readResponse.Results.Count, readValues.Count);
                for (var i = 0; i < resultCount; i++)
                {
                    if (StatusCode.IsGood(readResponse.Results[i].StatusCode))
                    {
                        var dataValue = readResponse.Results[i];
                        if (monitoredItems[offset + i].Handle is RegisteredSubjectProperty property)
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

            _logger.LogInformation("Successfully read initial values of {Count} nodes", itemCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read initial values for monitored items");
            throw;
        }
    }

    public Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<Action?>(null);

    /// <summary>
    /// Writes property changes to the OPC UA server. If disconnected, changes are queued using ring buffer semantics.
    /// HOT PATH - optimized for the common case of direct write to connected session.
    /// </summary>
    public async ValueTask WriteToSourceAsync(IReadOnlyList<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Count is 0)
        {
            return;
        }

        // HOT PATH: Try direct write first (no lock for read)
        var session = _sessionManager.CurrentSession;
        if (session is not null)
        {
            try
            {
                await WriteToSourceAsync(changes, session, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OPC UA write failed, will queue if session lost.");
            }
        }

        // COLD PATH: No session or write failed - acquire semaphore and retry or queue
        try
        {
            // Use blocking wait (not async) to maintain ValueTask pattern
            // This is acceptable as it only blocks when session is unavailable (rare)
            _writeFlushSemaphore.Wait(cancellationToken);
            try
            {
                // Re-check session after acquiring semaphore (may have reconnected)
                session = _sessionManager.CurrentSession;
                if (session is not null)
                {
                    // Session available - flush queue first (FIFO), then write new changes
                    if (!_writeQueueManager.IsEmpty)
                    {
                        var pendingWrites = _writeQueueManager.DequeueAll();
                        await WriteToSourceAsync(pendingWrites, session, cancellationToken);
                    }

                    await WriteToSourceAsync(changes, session, cancellationToken);
                }
                else
                {
                    // Still no session - queue the changes
                    _writeQueueManager.EnqueueBatch(changes);
                    _logger.LogDebug("Queued {Count} writes (session unavailable)", changes.Count);
                }
            }
            finally
            {
                _writeFlushSemaphore.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Queue writes if cancelled
            _writeQueueManager.EnqueueBatch(changes);
            _logger.LogDebug("Queued {Count} writes (operation cancelled)", changes.Count);
        }
    }

    private async Task WriteToSourceAsync(IReadOnlyList<SubjectPropertyChange> changes, Session session, CancellationToken cancellationToken)
    {
        var count = changes.Count;
        if (count is 0)
        {
            return;
        }

        var chunkSize = (int)(session.OperationLimits?.MaxNodesPerWrite ?? DefaultChunkSize);
        chunkSize = chunkSize is 0 ? int.MaxValue : chunkSize;

        for (var offset = 0; offset < count; offset += chunkSize)
        {
            var take = Math.Min(chunkSize, count - offset);

            var writeValues = BuildWriteValues(changes, offset, take);
            if (writeValues.Count is 0)
            {
                continue;
            }

            var writeResponse = await session.WriteAsync(
                requestHeader: null,
                writeValues,
                cancellationToken);

            LogWriteFailures(changes, writeValues, writeResponse.Results, offset);
        }
    }


    private WriteValueCollection BuildWriteValues(IReadOnlyList<SubjectPropertyChange> changes, int offset, int take)
    {
        var writeValues = new WriteValueCollection(take);

        for (var i = 0; i < take; i++)
        {
            var change = changes[offset + i];
            if (!change.Property.TryGetPropertyData(OpcVariableKey, out var v) || v is not NodeId nodeId)
            {
                continue;
            }

            var registeredProperty = change.Property.GetRegisteredProperty();
            if (!registeredProperty.HasSetter)
            {
                continue;
            }

            var value = _configuration.ValueConverter.ConvertToNodeValue(
                change.GetNewValue<object?>(),
                registeredProperty);

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

        return writeValues;
    }

    private void LogWriteFailures(
        IReadOnlyList<SubjectPropertyChange> changes,
        WriteValueCollection writeValues,
        StatusCodeCollection results,
        int offset)
    {
        for (var i = 0; i < Math.Min(results.Count, writeValues.Count); i++)
        {
            if (StatusCode.IsBad(results[i]))
            {
                var change = changes[offset + i];
                _logger.LogError(
                    "Failed to write {PropertyName} (NodeId: {NodeId}): {StatusCode}",
                    change.Property.Name,
                    writeValues[i].NodeId,
                    results[i]);
            }
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
            cancellationToken);

        return nodeProperties[0];
    }

    private void CleanUpPropertyVariableData()
    {
        foreach (var property in _propertiesWithOpcData)
        {
            try
            {
                property.SetPropertyData(OpcVariableKey, null);
            }
            catch { /* Ignore cleanup exceptions */ }
        }

        _propertiesWithOpcData.Clear();
    }

    public override void Dispose()
    {
        // Set disposed flag first to prevent new operations (thread-safe)
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // Already disposed
        }

        // Unsubscribe from events before disposing resources
        _sessionManager.SessionChanged -= OnSessionChanged;
        _sessionManager.ReconnectionCompleted -= OnReconnectionCompleted;

        try
        {
            base.Dispose();
        }
        finally
        {
            _sessionManager.Dispose();
            _subscriptionManager.Dispose();
            _pollingManager?.Dispose();
            _writeFlushSemaphore.Dispose();
        }
    }
}
