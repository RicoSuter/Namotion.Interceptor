using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client.Connection;
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
    private readonly HashSet<PropertyReference> _propertiesWithOpcData = [];

    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly SubscriptionHealthMonitor _subscriptionHealthMonitor;
    private readonly WriteFailureQueue _writeFailureQueue;

    private SessionManager? _sessionManager;
    private SourceUpdateBuffer? _updateBuffer;

    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)
    private volatile bool _isStarted;
    private int _reconnectingIterations; // Tracks health check iterations while reconnecting (for stall detection)

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
    
    public async Task<IDisposable?> StartListeningAsync(SourceUpdateBuffer updateBuffer, CancellationToken cancellationToken)
    {
        Reset();

        _updateBuffer = updateBuffer;
        _logger.LogInformation("Connecting to OPC UA server at {ServerUrl}.", _configuration.ServerUrl);

        _sessionManager = new SessionManager(this, updateBuffer, _configuration, _logger);
        
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

        _isStarted = true;
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
        // Single-threaded health check loop. Coordinates with automatic reconnection via IsReconnecting flag.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sessionManager = _sessionManager; // Capture reference to avoid TOCTOU
                if (sessionManager is not null && _isStarted)
                {
                    var isReconnecting = sessionManager.IsReconnecting;
                    if (isReconnecting)
                    {
                        var iterations = Interlocked.Increment(ref _reconnectingIterations);
                        if (iterations > 10)
                        {
                            // Timeout: 10 iterations × health check interval (default 10s) = ~100s
                            if (sessionManager.TryForceResetIfStalled())
                            {
                                _logger.LogWarning(
                                    "Stall confirmed: Reconnecting flag reset to allow manual recovery. " +
                                    "Session will be recreated on next health check.");
                            }

                            Interlocked.Exchange(ref _reconnectingIterations, 0);
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref _reconnectingIterations, 0); // Reset when not reconnecting
                    }
                    
                    var currentSession = sessionManager.CurrentSession;
                    if (currentSession is not null)
                    {
                        // Temporal separation: subscriptions added to collection AFTER initialization (see SubscriptionManager.cs:112)
                        await _subscriptionHealthMonitor.CheckAndHealSubscriptionsAsync(
                            sessionManager.Subscriptions,
                            stoppingToken).ConfigureAwait(false);
                    }
                    else if (!isReconnecting)
                    {
                        _logger.LogWarning(
                            "OPC UA session is dead with no active reconnection. " +
                            "SessionReconnectHandler likely timed out after {Timeout}ms. " +
                            "Restarting session manager...",
                            _configuration.ReconnectHandlerTimeout);

                        await ReconnectSessionAsync(stoppingToken).ConfigureAwait(false);
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
                // Log error but continue health monitoring loop
                _logger.LogError(ex, "Error during health check or session restart. Will retry.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("OPC UA client has stopped.");
    }

    /// <summary>
    /// Restarts the session after SessionReconnectHandler timeout.
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

        try
        {
            _logger.LogInformation(
                "Restarting OPC UA session after SessionReconnectHandler timeout ({Timeout}ms)...",
                _configuration.ReconnectHandlerTimeout);

            // Start collecting updates - any incoming subscription notifications will be buffered
            // until we complete the full state reload
            _updateBuffer?.StartBuffering();

            // Create new session (CreateSessionAsync disposes old session internally)
            var application = _configuration.CreateApplicationInstance();
            var session = await sessionManager.CreateSessionAsync(application, _configuration, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("New OPC UA session created successfully.");

            // Recreate subscriptions using cached monitored items
            if (_initialMonitoredItems is not null && _initialMonitoredItems.Count > 0)
            {
                await sessionManager.CreateSubscriptionsAsync(_initialMonitoredItems, session, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Subscriptions recreated successfully with {Count} monitored items.",
                    _initialMonitoredItems.Count);
            }

            // Flush any queued writes after successful reconnection
            // We're already in async context, so just await directly (no Task.Run needed)
            try
            {
                await FlushQueuedWritesAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to flush queued writes after session restart.");
            }

            // Load complete state and replay buffered updates (queue-read-replay pattern)
            // This ensures no data loss from values that changed during the disconnection period
            if (_updateBuffer is not null)
            {
                await _updateBuffer.CompleteInitializationAsync(cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Session restart complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart session. Will retry on next health check.");
            throw; // Re-throw to trigger retry in ExecuteAsync
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
    /// Writes changes to OPC UA server. Queues with ring buffer semantics if disconnected.
    /// </summary>
    public async ValueTask WriteToSourceAsync(IReadOnlyList<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Count is 0)
        {
            return;
        }

        var session = _sessionManager?.CurrentSession;
        if (session is null || !session.Connected)
        {
            _writeFailureQueue.EnqueueBatch(changes);
            return;
        }

        try
        {
            var succeeded = await FlushQueuedWritesAsync(session, cancellationToken).ConfigureAwait(false);
            if (succeeded)
            {
                await TryWriteToSourceWithoutFlushAsync(changes, session, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _writeFailureQueue.EnqueueBatch(changes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write {Count} changes to OPC UA server, queuing for retry.", changes.Count);
            _writeFailureQueue.EnqueueBatch(changes);
        }
    }

    /// <summary>
    /// Flushes pending writes from the queue.
    /// Returns true if flush succeeded (or queue was empty), false if flush failed.
    /// </summary>
    internal async Task<bool> FlushQueuedWritesAsync(Session session, CancellationToken cancellationToken)
    {
        try
        {
            await _writeFlushSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        try
        {
            if (_writeFailureQueue.IsEmpty)
            {
                return true;
            }

            var pendingWrites = _writeFailureQueue.DequeueAll();
            if (pendingWrites.Count > 0)
            {
                return await TryWriteToSourceWithoutFlushAsync(pendingWrites, session, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            try { _writeFlushSemaphore.Release(); } catch { /* might be disposed already */ }
        }
    }

    /// <summary>
    /// Writes changes in chunks. Re-queues only failed portion on partial failure.
    /// Returns true if all succeeded. Defensive session.Connected check handles staleness.
    /// </summary>
    private async Task<bool> TryWriteToSourceWithoutFlushAsync(IReadOnlyList<SubjectPropertyChange> changes, Session session, CancellationToken cancellationToken)
    {
        var count = changes.Count;
        if (count is 0)
        {
            return true;
        }

        if (!session.Connected)
        {
            _logger.LogWarning("Session not connected, queuing {Count} writes.", count);
            _writeFailureQueue.EnqueueBatch(changes);
            return false;
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

                var transientFailures = LogWriteFailures(changes, writeValues, writeResponse.Results, offset);

                // Re-queue changes that had transient errors for automatic retry
                if (transientFailures is not null && transientFailures.Count > 0)
                {
                    _logger.LogInformation(
                        "Re-queuing {Count} writes with transient errors for automatic retry.",
                        transientFailures.Count);
                    _writeFailureQueue.EnqueueBatch(transientFailures);
                }
            }
            catch (Exception ex)
            {
                // Network/communication failure - re-queue only the remaining changes from this offset onwards
                var remainingCount = count - offset;
                var remainingChanges = new List<SubjectPropertyChange>(remainingCount);
                for (var i = offset; i < count; i++)
                {
                    remainingChanges.Add(changes[i]);
                }

                _logger.LogWarning(ex,
                    "OPC UA write communication failed at offset {Offset}, re-queuing {Count} remaining changes.",
                    offset, remainingCount);

                _writeFailureQueue.EnqueueBatch(remainingChanges);
                return false; // Indicate failure
            }
        }

        return true; // All writes succeeded
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

    /// <summary>
    /// Determines if a write failure status code represents a transient error that should be retried.
    /// Returns true for transient errors (connectivity, timeouts, server load), false for permanent errors (invalid node, type mismatch).
    /// </summary>
    private static bool IsTransientWriteError(StatusCode statusCode)
    {
        // Permanent design-time errors - don't retry
        if (statusCode == StatusCodes.BadNodeIdUnknown ||
            statusCode == StatusCodes.BadAttributeIdInvalid ||
            statusCode == StatusCodes.BadTypeMismatch ||
            statusCode == StatusCodes.BadWriteNotSupported ||
            statusCode == StatusCodes.BadUserAccessDenied ||
            statusCode == StatusCodes.BadNotWritable)
        {
            return false;
        }

        // Transient connectivity/server errors - retry
        // Examples: BadSessionIdInvalid, BadConnectionClosed, BadServerNotConnected,
        // BadTimeout, BadRequestTimeout, BadTooManyOperations, BadOutOfService
        return StatusCode.IsBad(statusCode);
    }

    /// <summary>
    /// Logs write failures and collects changes with transient errors for retry.
    /// Returns list of changes that had transient errors and should be re-queued.
    /// </summary>
    private List<SubjectPropertyChange>? LogWriteFailures(
        IReadOnlyList<SubjectPropertyChange> changes,
        WriteValueCollection writeValues,
        StatusCodeCollection results,
        int offset)
    {
        List<SubjectPropertyChange>? transientFailures = null;

        for (var i = 0; i < Math.Min(results.Count, writeValues.Count); i++)
        {
            if (StatusCode.IsBad(results[i]))
            {
                var change = changes[offset + i];
                var statusCode = results[i];
                var isTransient = IsTransientWriteError(statusCode);

                if (isTransient)
                {
                    _logger.LogWarning(
                        "Transient write failure for {PropertyName} (NodeId: {NodeId}): {StatusCode}. Will retry.",
                        change.Property.Name,
                        writeValues[i].NodeId,
                        statusCode);

                    transientFailures ??= [];
                    transientFailures.Add(change);
                }
                else
                {
                    _logger.LogError(
                        "Permanent write failure for {PropertyName} (NodeId: {NodeId}): {StatusCode}. Not retrying.",
                        change.Property.Name,
                        writeValues[i].NodeId,
                        statusCode);
                }
            }
        }

        return transientFailures;
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
        _initialMonitoredItems = null;
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
            await sessionManager.DisposeAsync().ConfigureAwait(false);
            _sessionManager = null;
        }

        // Clean up property data to prevent memory leaks
        // This ensures that property data associated with this OpcUaNodeIdKey is cleared
        // even if properties are reused across multiple source instances
        CleanupPropertyData();
        Dispose();

        // Dispose semaphore directly - any in-flight flush will handle ObjectDisposedException
        // During shutdown, losing in-flight writes is acceptable
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
