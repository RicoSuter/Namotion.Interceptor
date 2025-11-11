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

    internal string OpcVariableKey { get; } = "OpcVariable:" + Guid.NewGuid();
    
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
        var session = await _sessionManager.CreateSessionAsync(application, _configuration, cancellationToken);

        _logger.LogInformation("Connected to OPC UA server successfully.");

        var rootNode = await TryGetRootNodeAsync(session, cancellationToken);
        if (rootNode is not null)
        {
            var monitoredItems = await _subjectLoader.LoadSubjectAsync(_subject, rootNode, session, cancellationToken);
            if (monitoredItems.Count > 0)
            {
                _initialMonitoredItems = monitoredItems;
                await _sessionManager.CreateSubscriptionsAsync(monitoredItems, session, cancellationToken);
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
                cancellationToken);

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
                    await _subscriptionHealthMonitor.CheckAndHealSubscriptionsAsync(_sessionManager.Subscriptions, stoppingToken);
                }

                await Task.Delay(_configuration.SubscriptionHealthCheckInterval, stoppingToken);
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
            var references = await BrowseNodeAsync(session, ObjectIds.ObjectsFolder, cancellationToken);
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
                await FlushQueuedWritesAsync(session, cancellationToken);
                await WriteToSourceAsync(changes, session, cancellationToken);
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

        var session = _sessionManager?.CurrentSession;
        if (session is not null)
        {
            Task.Run(async () =>
            {
                try
                {
                    await FlushQueuedWritesAsync(session, _stoppingToken);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Failed to flush pending OPC UA writes after reconnection.");
                }
            });
        }
    }

    private async Task FlushQueuedWritesAsync(Session session, CancellationToken cancellationToken)
    {
        try
        {
            // Wait for semaphore with cancellation support
            await _writeFlushSemaphore.WaitAsync(cancellationToken);
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
                        await WriteToSourceAsync(pendingWrites, session, cancellationToken);

                        _logger.LogInformation("Successfully flushed {Count} pending OPC UA writes after reconnection.", pendingWrites.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "OPC UA queue flusing failed, putting back.");
                        _writeFailureQueue.EnqueueBatch(pendingWrites);
                    }
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

    private void Reset()
    {
        _initialMonitoredItems = null;
        
        if (_sessionManager is not null)
        {
            _sessionManager.ReconnectionCompleted -= OnReconnectionCompleted;
        }

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
            await sessionManager.DisposeAsync();
        }
        
        Dispose();
        
        _writeFlushSemaphore.Dispose();
    }
}
