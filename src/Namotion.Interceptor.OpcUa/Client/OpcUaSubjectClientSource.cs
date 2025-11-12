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
    private readonly List<PropertyReference> _propertiesWithOpcData = [];

    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly SubscriptionHealthMonitor _subscriptionHealthMonitor;
    private readonly WriteFailureQueue _writeFailureQueue;

    private SessionManager? _sessionManager;

    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)
    private int _reconnectingIterations; // Tracks health check iterations while reconnecting (for stall detection)
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

        _sessionManager = new SessionManager(updater, _configuration, _logger);
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
        _stoppingToken = stoppingToken;

        // Single-threaded health check loop. Coordinates with automatic reconnection via IsReconnecting flag.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sessionManager = _sessionManager; // Capture reference to avoid TOCTOU
                if (sessionManager is not null)
                {
                    var currentSession = sessionManager.CurrentSession;
                    var isReconnecting = sessionManager.IsReconnecting;

                    // Stall detection: Track consecutive iterations where isReconnecting = true
                    if (isReconnecting)
                    {
                        var iterations = Interlocked.Increment(ref _reconnectingIterations);

                        // Timeout: 10 iterations × health check interval (default 10s) = ~100s
                        if (iterations > 10)
                        {
                            _logger.LogError(
                                "OPC UA reconnection stalled for {Iterations} health check iterations " +
                                "(~{Seconds}s). OnReconnectComplete callback likely never fired. " +
                                "Attempting stall recovery with synchronized flag reset.",
                                iterations,
                                iterations * (_configuration.SubscriptionHealthCheckInterval.TotalSeconds));

                            // Use synchronized reset to prevent race with delayed OnReconnectComplete
                            if (sessionManager.TryForceResetIfStalled())
                            {
                                _logger.LogWarning(
                                    "Stall confirmed: reconnecting flag reset to allow manual recovery. " +
                                    "Session will be recreated on next health check.");
                                Interlocked.Exchange(ref _reconnectingIterations, 0);
                            }
                            else
                            {
                                _logger.LogInformation(
                                    "Stall recovery skipped: reconnection completed while acquiring lock. " +
                                    "Session restoration successful.");
                                Interlocked.Exchange(ref _reconnectingIterations, 0);
                            }
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref _reconnectingIterations, 0); // Reset when not reconnecting
                    }

                    if (currentSession is null && !isReconnecting)
                    {
                        // SessionReconnectHandler timed out. Restart session manually.
                        // !isReconnecting prevents conflicts with automatic reconnection.
                        _logger.LogWarning(
                            "OPC UA session is dead with no active reconnection. " +
                            "SessionReconnectHandler likely timed out after {Timeout}ms. " +
                            "Restarting session manager...",
                            _configuration.ReconnectHandlerTimeout);

                        await ReconnectSessionAsync(stoppingToken).ConfigureAwait(false);
                    }
                    else if (currentSession is not null)
                    {
                        // Temporal separation: subscriptions added to collection AFTER initialization (see SubscriptionManager.cs:112)
                        await _subscriptionHealthMonitor.CheckAndHealSubscriptionsAsync(
                            sessionManager.Subscriptions,
                            stoppingToken).ConfigureAwait(false);
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

            // Create new session (CreateSessionAsync disposes old session internally)
            var application = _configuration.CreateApplicationInstance();
            var session = await sessionManager.CreateSessionAsync(application, _configuration, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("New OPC UA session created successfully.");

            // Recreate subscriptions using cached monitored items
            if (_initialMonitoredItems is not null && _initialMonitoredItems.Count > 0)
            {
                await sessionManager.CreateSubscriptionsAsync(_initialMonitoredItems, session, cancellationToken)
                    .ConfigureAwait(false);

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
    /// Session staleness handled defensively via session.Connected checks.
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
            // Flush old pending writes first - if this fails, don't attempt new writes
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

        var cancellationToken = _stoppingToken;

        // Task.Run needed: synchronous event handler, cannot await.
        // Safe: All exceptions caught, semaphore coordinates concurrent access, cancellation coordinated with disposal.
        Task.Run(async () =>
        {
            try
            {
                var session = _sessionManager?.CurrentSession;
                if (session is not null)
                {
                    if (!session.Connected)
                    {
                        _logger.LogDebug("Session disconnected before flush could execute, will retry on next reconnection");
                        return;
                    }

                    await FlushQueuedWritesAsync(session, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to flush pending OPC UA writes after reconnection.");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Flushes pending writes from the queue.
    /// Returns true if flush succeeded (or queue was empty), false if flush failed.
    /// </summary>
    private async Task<bool> FlushQueuedWritesAsync(Session session, CancellationToken cancellationToken)
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
            _writeFlushSemaphore.Release();
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

                LogWriteFailures(changes, writeValues, writeResponse.Results, offset);
            }
            catch (Exception ex)
            {
                // Partial write failure - re-queue only the remaining changes from this offset onwards
                var remainingCount = count - offset;
                var remainingChanges = new List<SubjectPropertyChange>(remainingCount);
                for (var i = offset; i < count; i++)
                {
                    remainingChanges.Add(changes[i]);
                }

                _logger.LogWarning(ex, 
                    "OPC UA write failed at offset {Offset}, re-queuing {Count} remaining changes.",
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
        // No disposal needed - SubjectSourceBackgroundService disposes session manager before calling Reset().
        _initialMonitoredItems = null;

        if (_sessionManager is not null)
        {
            _sessionManager.ReconnectionCompleted -= OnReconnectionCompleted;
            _sessionManager = null;
        }

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
