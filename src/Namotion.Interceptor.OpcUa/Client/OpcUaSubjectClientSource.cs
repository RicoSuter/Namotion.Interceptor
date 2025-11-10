using System.Collections.Immutable;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client.Polling;
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
    private readonly OpcUaSubscriptionHealthMonitor _subscriptionHealthMonitor;
    
    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)
    private CancellationToken _stoppingToken;

    internal string OpcVariableKey { get; } = "OpcVariable:" + Guid.NewGuid();
    
    public OpcUaSubscriptionManager SubscriptionManager { get; }

    public OpcUaSessionManager SessionManager { get; }

    public OpcUaWriteQueueManager WriteQueueManager { get; }
    
    public PollingManager? PollingManager { get; private set; }

    public OpcUaSubjectClientSource(IInterceptorSubject subject, OpcUaClientConfiguration configuration, ILogger<OpcUaSubjectClientSource> logger)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        configuration.Validate();

        _subject = subject;
        _logger = logger;
        _configuration = configuration;

        SessionManager = new OpcUaSessionManager(logger, configuration);
        WriteQueueManager = new OpcUaWriteQueueManager(_configuration.WriteQueueSize, logger);
        SubscriptionManager = new OpcUaSubscriptionManager(configuration, logger);
   
        _subjectLoader = new OpcUaSubjectLoader(configuration, _propertiesWithOpcData, this, logger);
        _subscriptionHealthMonitor = new OpcUaSubscriptionHealthMonitor(logger);

        SessionManager.SessionChanged += OnSessionChanged;
        SessionManager.ReconnectionCompleted += OnReconnectionCompleted;
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property) =>
        _configuration.SourcePathProvider.IsPropertyIncluded(property);

    public async Task<IDisposable?> StartListeningAsync(ISubjectUpdater updater, CancellationToken cancellationToken)
    {
        SubscriptionManager.SetUpdater(updater);

        if (_configuration.EnablePollingFallback && PollingManager == null)
        {
            var pollingManager = new PollingManager(
                logger: _logger,
                sessionManager: SessionManager,
                updater: updater,
                pollingInterval: _configuration.PollingInterval,
                batchSize: _configuration.PollingBatchSize,
                disposalTimeout: _configuration.PollingDisposalTimeout,
                circuitBreakerThreshold: _configuration.PollingCircuitBreakerThreshold,
                circuitBreakerCooldown: _configuration.PollingCircuitBreakerCooldown
            );
            pollingManager.Start();

            SubscriptionManager.SetPollingManager(pollingManager);
            PollingManager = pollingManager;
        }
        
        _logger.LogInformation("Connecting to OPC UA server at {ServerUrl}.", _configuration.ServerUrl);

        await SessionManager.CloseSessionAsync();
        CleanUpPropertyVariableData();
        SubscriptionManager.Cleanup();
        
        var application = _configuration.CreateApplicationInstance();
        var session = await SessionManager.CreateSessionAsync(application, _configuration, cancellationToken);

        SubscriptionManager.Clear();
        _propertiesWithOpcData.Clear();

        _logger.LogInformation("Connected to OPC UA server successfully.");

        var rootNode = await TryGetRootNodeAsync(session, cancellationToken);
        if (rootNode is not null)
        {
            var monitoredItems = await _subjectLoader.LoadSubjectAsync(_subject, rootNode, session, cancellationToken);
            if (monitoredItems.Count > 0)
            {
                await SubscriptionManager.CreateBatchedSubscriptionsAsync(monitoredItems, session, cancellationToken);
                
                _logger.LogInformation("Created {SubscriptionCount} subscriptions monitoring {Subscribed} items ({Polled} via polling).",
                    SubscriptionManager.Subscriptions.Count,
                    SubscriptionManager.MonitoredItems.Count,
                    PollingManager?.PollingItemCount ?? 0);
            }
            else
            {
                _logger.LogWarning("No monitored items found, using polling {Polled} items.", PollingManager?.PollingItemCount ?? 0);
            }
        }
        else
        {
            _logger.LogWarning("Connected to OPC UA server successfully but could not find root node.");
        }

        return Task.FromResult<IDisposable?>(null);
    }
    
    public async Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken)
    {
        var session = SessionManager.CurrentSession;
        if (session is null)
        {
            throw new InvalidOperationException("No active OPC UA session available.");
        }
        
        var monitoredItems = SubscriptionManager.MonitoredItems.Values.ToArray();
        var itemCount = monitoredItems.Length;
            
        var chunkSize = (int)(session.OperationLimits?.MaxNodesPerRead ?? DefaultChunkSize);
        chunkSize = chunkSize is 0 ? int.MaxValue : chunkSize;

        var result = new Dictionary<RegisteredSubjectProperty, DataValue>();
        for (var offset = 0; offset < itemCount; offset += chunkSize)
        {
            var take = Math.Min(chunkSize, itemCount - offset);
            var readValues = new ReadValueIdCollection(take);

            for (var i = 0; i < take; i++)
            {
                readValues.Add(new ReadValueId
                {
                    NodeId = monitoredItems[offset + i].monitoredItem.StartNodeId,
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
                    if (monitoredItems[offset + i].monitoredItem.Handle is RegisteredSubjectProperty property)
                    {
                        result[property] = dataValue;
                    }
                }
            }
        }
            
        _logger.LogInformation("Successfully read {Count} OPC UA nodes from server.", itemCount);
        return () =>
        {
            foreach (var (property, dataValue) in result)
            {
                var value = _configuration.ValueConverter.ConvertToPropertyValue(dataValue.Value, property);
                property.SetValueFromSource(this, dataValue.SourceTimestamp, value);
            }

            _logger.LogInformation("Updated {Count} properties with OPC UA node values.", itemCount);
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken; // Store for event handlers

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _subscriptionHealthMonitor.CheckAndHealSubscriptionsAsync(SubscriptionManager.Subscriptions, stoppingToken);
                await Task.Delay(_configuration.SubscriptionHealthCheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
            }
        }

        await SessionManager.CloseSessionAsync();
        CleanUpPropertyVariableData();
        SubscriptionManager.Cleanup();
        
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
            var transferredSubscriptions = e.Session.Subscriptions.ToImmutableArray();
            if (transferredSubscriptions.Length > 0)
            {
                SubscriptionManager.UpdateTransferredSubscriptions(transferredSubscriptions);
                _logger.LogInformation("OPC UA session reconnected. Transferred {Count} subscriptions by OPC UA stack.", transferredSubscriptions.Length);
            }
        }
        else if (e.Session is null)
        {
            _logger.LogWarning("OPC UA session disconnected permanently.");
        }
    }

    private void OnReconnectionCompleted(object? sender, EventArgs e)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
        {
            return;
        }

        // Queue async work with continuation to handle exceptions
        FlushQueuedWritesAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogError(t.Exception, "Failed to flush pending OPC UA writes after reconnection.");
            }
        }, TaskScheduler.Default);
    }

    private async Task FlushQueuedWritesAsync()
    {
        try
        {
            // Wait for semaphore with cancellation support
            await _writeFlushSemaphore.WaitAsync(_stoppingToken);
            try
            {
                if (WriteQueueManager.IsEmpty)
                {
                    return;
                }

                var session = SessionManager.CurrentSession;
                if (session is null)
                {
                    return;
                }

                var pendingWrites = WriteQueueManager.DequeueAll();
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
        var session = SessionManager.CurrentSession;
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
                session = SessionManager.CurrentSession;
                if (session is not null)
                {
                    // Session available - flush queue first (FIFO), then write new changes
                    if (!WriteQueueManager.IsEmpty)
                    {
                        var pendingWrites = WriteQueueManager.DequeueAll();
                        await WriteToSourceAsync(pendingWrites, session, cancellationToken);
                    }

                    await WriteToSourceAsync(changes, session, cancellationToken);
                }
                else
                {
                    // Still no session - queue the changes
                    WriteQueueManager.EnqueueBatch(changes);
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
            WriteQueueManager.EnqueueBatch(changes);
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
        SessionManager.SessionChanged -= OnSessionChanged;
        SessionManager.ReconnectionCompleted -= OnReconnectionCompleted;

        try
        {
            base.Dispose();
        }
        finally
        {
            SessionManager.Dispose();
            SubscriptionManager.Dispose();
            PollingManager?.Dispose();
            _writeFlushSemaphore.Dispose();
        }
    }
}
