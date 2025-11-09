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

    private readonly Lock _writeFlushLock = new();

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly List<PropertyReference> _propertiesWithOpcData = [];
    
    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly OpcUaSubscriptionManager _subscriptionManager;
    private readonly OpcUaSessionManager _sessionManager;
    private readonly OpcUaWriteQueueManager _writeQueueManager;
    
    private bool _disposed;

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
        _subscriptionManager = new OpcUaSubscriptionManager(configuration, logger);
        _sessionManager = new OpcUaSessionManager(logger);
        _writeQueueManager = new OpcUaWriteQueueManager(_configuration.WriteQueueSize, logger);

        _sessionManager.SessionChanged += OnSessionChanged;
        _sessionManager.ReconnectionCompleted += OnReconnectionCompleted;
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property) =>
        _configuration.SourcePathProvider.IsPropertyIncluded(property);

    public Task<IDisposable?> StartListeningAsync(ISubjectUpdater updater, CancellationToken cancellationToken)
    {
        _subscriptionManager.SetUpdater(updater);
        return Task.FromResult<IDisposable?>(null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                    }

                    _logger.LogInformation("OPC UA client initialization complete. Monitoring {Count} items.", monitoredItems.Count);
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
        if (_disposed)
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
    /// Handles reconnection completion. Flushes pending writes synchronously within lock.
    /// This is acceptable as async void event handler since all work is synchronous.
    /// </summary>
    private void OnReconnectionCompleted(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            lock (_writeFlushLock)
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
                    WriteToSourceSync(pendingWrites, session);
                    _logger.LogInformation("Successfully flushed {Count} pending OPC UA writes after reconnection.", pendingWrites.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush pending OPC UA writes after reconnection.");
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

        // COLD PATH: No session or write failed - queue the changes
        lock (_writeFlushLock)
        {
            // Re-check session after acquiring lock (may have reconnected)
            session = _sessionManager.CurrentSession;
            if (session is not null)
            {
                // Session available - flush queue first (FIFO), then write new changes
                if (!_writeQueueManager.IsEmpty)
                {
                    var pendingWrites = _writeQueueManager.DequeueAll();
                    WriteToSourceSync(pendingWrites, session);
                }

                WriteToSourceSync(changes, session);
            }
            else
            {
                // Still no session - queue the changes
                _writeQueueManager.EnqueueBatch(changes);
                _logger.LogDebug("Queued {Count} writes (session unavailable)", changes.Count);
            }
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

    private void WriteToSourceSync(IReadOnlyList<SubjectPropertyChange> changes, Session session)
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

            // Synchronous write for cold path (flush/queue retry)
            _ = session.Write(
                requestHeader: null,
                writeValues,
                out var results,
                out _);

            LogWriteFailures(changes, writeValues, results, offset);
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
        if (_disposed)
            return;

        _disposed = true;
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
        }
    }
}
