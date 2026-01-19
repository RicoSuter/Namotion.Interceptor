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

internal sealed class OpcUaSubjectClientSource : BackgroundService, ISubjectSource, IAsyncDisposable
{
    private const int DefaultChunkSize = 512;

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;

    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly SubscriptionHealthMonitor _subscriptionHealthMonitor;

    private SessionManager? _sessionManager;
    private SubjectPropertyWriter? _propertyWriter;

    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)
    private volatile bool _isStarted;
    private int _reconnectingIterations; // Tracks health check iterations while reconnecting (for stall detection)

    // Diagnostics tracking - accessed from multiple threads via Diagnostics property
    // Note: DateTimeOffset? cannot be volatile, but reads are atomic on 64-bit systems
    // and visibility is ensured by the memory barriers in Interlocked operations
    private long _totalReconnectionAttempts;
    private long _successfulReconnections;
    private long _failedReconnections;
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
    internal DateTimeOffset? LastConnectedAt { get; private set; }
    internal DateTimeOffset? LastDisconnectedAt { get; private set; }
    internal int ConsecutiveHealthCheckErrors { get; private set; }

    /// <summary>
    /// Called by SessionManager when a reconnection attempt starts (via SDK's SessionReconnectHandler).
    /// Updates diagnostics metrics to track the reconnection attempt and disconnection time.
    /// </summary>
    internal void RecordReconnectionAttemptStart()
    {
        Interlocked.Increment(ref _totalReconnectionAttempts);
        LastDisconnectedAt ??= DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Called by SessionManager when SDK's SessionReconnectHandler successfully reconnects.
    /// Updates diagnostics metrics to track the successful reconnection.
    /// Note: Does not increment TotalReconnectionAttempts - that's done in RecordReconnectionAttemptStart.
    /// </summary>
    internal void RecordSdkReconnectionSuccess()
    {
        Interlocked.Increment(ref _successfulReconnections);
        LastConnectedAt = DateTimeOffset.UtcNow;
        LastDisconnectedAt = null;
    }

    private bool IsReconnecting => _sessionManager?.IsReconnecting == true;

    private void RemoveItemsForSubject(IInterceptorSubject subject)
    {
        _sessionManager?.SubscriptionManager.RemoveItemsForSubject(subject);
        _sessionManager?.PollingManager?.RemoveItemsForSubject(subject);
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
            onReleasing: property => property.RemovePropertyData(OpcUaNodeIdKey),
            onSubjectDetaching: OnSubjectDetaching);
        _subjectLoader = new OpcUaSubjectLoader(configuration, _ownership, this, logger);
        _subscriptionHealthMonitor = new SubscriptionHealthMonitor(logger);
    }

    private void OnSubjectDetaching(IInterceptorSubject subject)
    {
        // Skip cleanup during reconnection (subscriptions being transferred)
        if (IsReconnecting)
        {
            return;
        }

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

        LastConnectedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("Connected to OPC UA server successfully.");

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
    internal IReadOnlyList<MonitoredItem> CreateMonitoredItemsForReconnection()
    {
        var ownedProperties = GetOwnedPropertiesWithNodeIds();
        var monitoredItems = new List<MonitoredItem>(ownedProperties.Count);

        foreach (var (property, nodeId) in ownedProperties)
        {
            var opcUaNodeAttribute = property.TryGetOpcUaNodeAttribute();
            var monitoredItem = new MonitoredItem(_configuration.TelemetryContext)
            {
                StartNodeId = nodeId,
                AttributeId = Opc.Ua.Attributes.Value,
                MonitoringMode = MonitoringMode.Reporting,
                SamplingInterval = opcUaNodeAttribute?.SamplingInterval ?? _configuration.DefaultSamplingInterval,
                QueueSize = opcUaNodeAttribute?.QueueSize ?? _configuration.DefaultQueueSize,
                DiscardOldest = opcUaNodeAttribute?.DiscardOldest ?? _configuration.DefaultDiscardOldest,
                Handle = property
            };

            monitoredItems.Add(monitoredItem);
        }

        return monitoredItems;
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
                    var stallDetected = false;
                    if (isReconnecting)
                    {
                        var iterations = Interlocked.Increment(ref _reconnectingIterations);
                        var stallThreshold = _configuration.StallDetectionIterations;
                        if (iterations > stallThreshold)
                        {
                            // Timeout: StallDetectionIterations × SubscriptionHealthCheckInterval
                            if (sessionManager.TryForceResetIfStalled())
                            {
                                _logger.LogWarning(
                                    "Stall confirmed: Reconnecting flag reset to allow manual recovery. " +
                                    "Triggering immediate session restart.");
                                stallDetected = true;
                            }

                            Interlocked.Exchange(ref _reconnectingIterations, 0);
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref _reconnectingIterations, 0); // Reset when not reconnecting
                    }

                    // Trigger manual reconnection if stall was detected
                    if (stallDetected)
                    {
                        await ReconnectSessionAsync(stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var currentSession = sessionManager.CurrentSession;
                        var sessionIsConnected = currentSession?.Connected ?? false;
                        if (currentSession is not null && sessionIsConnected)
                        {
                            // Temporal separation: subscriptions added to collection AFTER initialization (see SubscriptionManager.cs:112)
                            await _subscriptionHealthMonitor.CheckAndHealSubscriptionsAsync(
                                sessionManager.Subscriptions,
                                stoppingToken).ConfigureAwait(false);
                        }
                        else if (!isReconnecting)
                        {
                            // Session is either null or disconnected, and SDK reconnect handler is not active
                            _logger.LogWarning(
                                "OPC UA session is dead (session={HasSession}, connected={IsConnected}) with no active reconnection. " +
                                "SessionReconnectHandler likely timed out after {Timeout}ms. " +
                                "Restarting session manager...",
                                currentSession is not null,
                                sessionIsConnected,
                                _configuration.ReconnectHandlerTimeout);

                            await ReconnectSessionAsync(stoppingToken).ConfigureAwait(false);
                        }
                    }
                }

                ConsecutiveHealthCheckErrors = 0; // Reset on successful iteration
                await Task.Delay(_configuration.SubscriptionHealthCheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                ConsecutiveHealthCheckErrors++;

                // Exponential backoff with jitter: 5s, 10s, 20s, 30s (capped) + 0-2s random jitter
                // Jitter prevents thundering herd when multiple clients fail simultaneously
                var baseDelay = Math.Min(5 * Math.Pow(2, ConsecutiveHealthCheckErrors - 1), 30);
                var jitter = Random.Shared.NextDouble() * 2;
                var delaySeconds = baseDelay + jitter;

                _logger.LogError(ex,
                    "Error during health check or session restart (attempt {Attempt}). Retrying in {Delay:F1}s.",
                    ConsecutiveHealthCheckErrors, delaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken).ConfigureAwait(false);
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

        var propertyWriter = _propertyWriter;
        if (propertyWriter is null)
        {
            return;
        }

        Interlocked.Increment(ref _totalReconnectionAttempts);
        LastDisconnectedAt ??= DateTimeOffset.UtcNow; // Only set if not already set

        try
        {
            _logger.LogInformation(
                "Restarting OPC UA session after SessionReconnectHandler timeout ({Timeout}ms)...",
                _configuration.ReconnectHandlerTimeout);

            // Start collecting updates - any incoming subscription notifications will be buffered
            // until we complete the full state reload
            propertyWriter.StartBuffering();

            // Create new session (CreateSessionAsync disposes old session internally)
            var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);
            var session = await sessionManager.CreateSessionAsync(application, _configuration, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("New OPC UA session created successfully.");

            // Recreate MonitoredItems from owned properties (avoids memory leak from holding SDK objects)
            var monitoredItems = CreateMonitoredItemsForReconnection();
            if (monitoredItems.Count > 0)
            {
                await sessionManager.CreateSubscriptionsAsync(monitoredItems, session, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Subscriptions recreated successfully with {Count} monitored items.",
                    monitoredItems.Count);
            }

            await propertyWriter.CompleteInitializationAsync(cancellationToken).ConfigureAwait(false);

            Interlocked.Increment(ref _successfulReconnections);
            LastConnectedAt = DateTimeOffset.UtcNow;
            LastDisconnectedAt = null; // Clear disconnected timestamp on successful reconnection
            _logger.LogInformation("Session restart complete.");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedReconnections);
            _logger.LogError(ex, "Failed to restart session. Will retry on next health check.");

            // Clear the session so health check can trigger a new reconnection attempt
            await sessionManager.ClearSessionAsync(cancellationToken).ConfigureAwait(false);

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
            return ProcessWriteResults(writeResponse.Results, changes);
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
            if (!IsWritableOpcUaProperty(change))
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

    private bool IsWritableOpcUaProperty(SubjectPropertyChange change)
    {
        return change.Property.TryGetPropertyData(OpcUaNodeIdKey, out var nodeId)
            && nodeId is NodeId
            && change.Property.TryGetRegisteredProperty() is { HasSetter: true };
    }

    private WriteValueCollection CreateWriteValuesCollection(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var span = changes.Span;
        var writeValues = new WriteValueCollection(span.Length);

        for (var i = 0; i < span.Length; i++)
        {
            var change = span[i];
           
            if (!IsWritableOpcUaProperty(change))
            {
                continue;
            }

            if (!change.Property.TryGetPropertyData(OpcUaNodeIdKey, out var v) || v is not NodeId nodeId)
            {
                continue;
            }

            var registeredProperty = change.Property.GetRegisteredProperty();
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
    /// Returns true for transient errors (connectivity, timeouts, server load), false for permanent errors.
    /// </summary>
    private static bool IsTransientWriteError(StatusCode statusCode)
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
