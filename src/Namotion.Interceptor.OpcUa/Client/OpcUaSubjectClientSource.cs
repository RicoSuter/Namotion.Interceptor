using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal sealed class OpcUaSubjectClientSource : BackgroundService, ISubjectSource, IAsyncDisposable
{
    private const int DefaultChunkSize = 512;

    private readonly SemaphoreSlim _writeFlushSemaphore = new(1, 1);

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly List<PropertyReference> _propertiesWithOpcData = [];

    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly SubscriptionHealthMonitor _subscriptionHealthMonitor;
    private readonly WriteFailureQueue _writeFailureQueue;
    
    private OpcUaSessionManager? _sessionManager;
    
    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)
    private CancellationToken _stoppingToken;

    private IReadOnlyList<MonitoredItem>? _initialMonitoredItems;

    internal string OpcUaNodeIdKey { get; } = "OpcUaNodeId:" + Guid.NewGuid();
    
    public OpcUaSubjectClientSource(IInterceptorSubject subject, OpcUaClientConfiguration configuration, ILogger<OpcUaSubjectClientSource> logger)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        configuration.Validate();

        _subject = subject;
        _logger = logger;
        _configuration = configuration;

        _writeFailureQueue = new WriteFailureQueue(_configuration.WriteQueueSize, logger);
   
        _subjectLoader = new OpcUaSubjectLoader(configuration, _propertiesWithOpcData, this, logger);
        _subscriptionHealthMonitor = new SubscriptionHealthMonitor(logger);
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property) =>
        _configuration.SourcePathProvider.IsPropertyIncluded(property);

    public async Task<IDisposable?> StartListeningAsync(ISubjectUpdater updater, CancellationToken cancellationToken)
    {
        Reset();

        _logger.LogInformation("Connecting to OPC UA server at {ServerUrl}.", _configuration.ServerUrl);
        
        _sessionManager = new OpcUaSessionManager(updater, _configuration, _logger);
        _sessionManager.ReconnectionCompleted += OnReconnectionCompleted;
        
        var application = _configuration.CreateApplicationInstance();
        var session = await _sessionManager.CreateSessionAsync(application, _configuration, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Connected to OPC UA server successfully.");

        var rootNode = await TryGetRootNodeAsync(session, cancellationToken).ConfigureAwait(false);
        if (rootNode is not null)
        {
            var monitoredItems = await _subjectLoader.LoadSubjectAsync(_subject, rootNode, session, cancellationToken).ConfigureAwait(false);
            if (monitoredItems.Count > 0)
            {
                _initialMonitoredItems = monitoredItems;
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

        return _sessionManager;
    }
    
    public async Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken)
    {
        var initialMonitoredItems = _initialMonitoredItems;
        if (initialMonitoredItems is null)
        {
            return null;
        }

        var session = _sessionManager?.CurrentSession;
        if (session is null)
        {
            throw new InvalidOperationException("No active OPC UA session available.");
        }
        
        var itemCount = initialMonitoredItems.Count;
            
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
                    NodeId = initialMonitoredItems[offset + i].StartNodeId,
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
                    if (initialMonitoredItems[offset + i].Handle is RegisteredSubjectProperty property)
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
                if (_sessionManager?.CurrentSession is not null)
                {
                    // Health monitor only operates on subscriptions already in the collection
                    // Thread-safety: Temporal separation ensures subscriptions are fully initialized
                    // before being added to _sessionManager.Subscriptions (see OpcUaSubscriptionManager.cs:121)
                    await _subscriptionHealthMonitor.CheckAndHealSubscriptionsAsync(_sessionManager.Subscriptions, stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(_configuration.SubscriptionHealthCheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
            }
        }

        _logger.LogInformation("OPC UA client has stopped.");
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
    /// Writes property changes to the OPC UA server. If disconnected, changes are queued using ring buffer semantics.
    /// HOT PATH - optimized for the common case of direct write to connected session.
    /// </summary>
    public async ValueTask WriteToSourceAsync(IReadOnlyList<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Count is 0)
        {
            return;
        }

        var session = _sessionManager?.CurrentSession;
        if (session is not null)
        {
            try
            {
                await FlushQueuedWritesAsync(session, cancellationToken).ConfigureAwait(false);
                await WriteToSourceWithoutFlushAsync(changes, session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OPC UA write failed, changes queued.");

                // When flush or write failed, queue the changes to try to apply later
                _writeFailureQueue.EnqueueBatch(changes);
            }
        }
        else
        {
            // When session is not available, queue the changes to try to apply later
            _writeFailureQueue.EnqueueBatch(changes);
        }
    }

    private void OnReconnectionCompleted(object? sender, EventArgs e)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
        {
            return;
        }

        // Task.Run is intentional here (not a fire-and-forget anti-pattern):
        // - We're in a synchronous event handler context (cannot await)
        // - All exceptions are caught and logged (no unobserved exceptions)
        // - Semaphore in FlushQueuedWritesAsync coordinates with concurrent WriteToSourceAsync calls
        // - If concurrent write happens: it waits on semaphore, then its own flush is empty (early return)
        // - Session is resolved internally to avoid capturing potentially stale session reference
        Task.Run(async () =>
        {
            try
            {
                var session = _sessionManager?.CurrentSession;
                if (session is not null)
                {
                    // Validate session is still connected before flushing
                    // (defensive check in case session changed between capture and Task.Run execution)
                    if (!session.Connected)
                    {
                        _logger.LogDebug("Session disconnected before flush could execute, will retry on next reconnection");
                        return;
                    }

                    await FlushQueuedWritesAsync(session, _stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to flush pending OPC UA writes after reconnection.");
            }
        }, _stoppingToken);
    }

    private async Task FlushQueuedWritesAsync(Session session, CancellationToken cancellationToken)
    {
        try
        {
            // Wait for semaphore with cancellation support
            await _writeFlushSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_writeFailureQueue.IsEmpty)
                {
                    return;
                }
                
                var pendingWrites = _writeFailureQueue.DequeueAll();
                if (pendingWrites.Count > 0)
                {
                    try
                    {
                        await WriteToSourceWithoutFlushAsync(pendingWrites, session, cancellationToken).ConfigureAwait(false);

                        _logger.LogInformation("Successfully flushed {Count} pending OPC UA writes after reconnection.", pendingWrites.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "OPC UA queue flushing failed, re-queuing writes.");
                        _writeFailureQueue.EnqueueBatch(pendingWrites);
                    }
                }
            }
            finally
            {
                _writeFlushSemaphore.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Write flush cancelled during shutdown");
        }
    }

    private async Task WriteToSourceWithoutFlushAsync(IReadOnlyList<SubjectPropertyChange> changes, Session session, CancellationToken cancellationToken)
    {
        var count = changes.Count;
        if (count is 0)
        {
            return;
        }

        // Defensive check: verify session is still connected before expensive operation
        if (!session.Connected)
        {
            throw new ServiceResultException(StatusCodes.BadSessionClosed, "Session is not connected");
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

            try
            {
                var writeResponse = await session.WriteAsync(
                    requestHeader: null,
                    writeValues,
                    cancellationToken).ConfigureAwait(false);

                LogWriteFailures(changes, writeValues, writeResponse.Results, offset);
            }
            catch (ObjectDisposedException)
            {
                // Session was disposed mid-operation - treat as disconnection
                throw new ServiceResultException(StatusCodes.BadSessionClosed, "Session disposed during write operation");
            }
        }
    }
    
    private WriteValueCollection BuildWriteValues(IReadOnlyList<SubjectPropertyChange> changes, int offset, int take)
    {
        var writeValues = new WriteValueCollection(take);

        for (var i = 0; i < take; i++)
        {
            var change = changes[offset + i];
            if (!change.Property.TryGetPropertyData(OpcUaNodeIdKey, out var v) || v is not NodeId nodeId)
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
            cancellationToken).ConfigureAwait(false);

        return nodeProperties[0];
    }

    private void Reset()
    {
        // Note: Session manager disposal is NOT needed here.
        // The SubjectSourceBackgroundService.ExecuteAsync retry loop calls DisposeAsync()
        // on the disposable returned by StartListeningAsync, which properly disposes the
        // session manager BEFORE Reset() is called on the next retry.
        // Reset() is called at the START of StartListeningAsync, where the old session
        // manager has already been disposed by the background service.

        _initialMonitoredItems = null;

        if (_sessionManager is not null)
        {
            // Unsubscribe from events to prevent leaks (defensive, already unsubscribed in DisposeAsync)
            _sessionManager.ReconnectionCompleted -= OnReconnectionCompleted;
            _sessionManager = null;
        }

        // Clean up property data before reloading
        // This is already done in DisposeAsync, but we do it here too for completeness
        // since we're about to reload properties with new OPC UA node mappings
        CleanupPropertyData();
    }

    public async ValueTask DisposeAsync()
    {
        // Set disposed flag first to prevent new operations (thread-safe)
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // Already disposed
        }

        var sessionManager = _sessionManager;
        if (sessionManager is not null)
        {
            sessionManager.ReconnectionCompleted -= OnReconnectionCompleted;
            await sessionManager.DisposeAsync().ConfigureAwait(false);
        }

        // Clean up property data to prevent memory leaks
        // This ensures that property data associated with this OpcUaNodeIdKey is cleared
        // even if properties are reused across multiple source instances
        CleanupPropertyData();
        Dispose();

        _writeFlushSemaphore.Dispose();
    }

    private void CleanupPropertyData()
    {
        foreach (var property in _propertiesWithOpcData)
        {
            property.RemovePropertyData(OpcUaNodeIdKey);
        }

        _propertiesWithOpcData.Clear();
    }
}
