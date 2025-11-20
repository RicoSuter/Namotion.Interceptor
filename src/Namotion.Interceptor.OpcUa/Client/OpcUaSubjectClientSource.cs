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

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly HashSet<PropertyReference> _propertiesWithOpcData = [];

    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly SubscriptionHealthMonitor _subscriptionHealthMonitor;

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

        var updateBuffer = _updateBuffer;
        if (updateBuffer is null)
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
            updateBuffer.StartBuffering();

            // Create new session (CreateSessionAsync disposes old session internally)
            var application = _configuration.CreateApplicationInstance();
            var session = await sessionManager.CreateSessionAsync(application, _configuration, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("New OPC UA session created successfully.");
            
            if (_initialMonitoredItems is not null && _initialMonitoredItems.Count > 0)
            {
                await sessionManager.CreateSubscriptionsAsync(_initialMonitoredItems, session, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Subscriptions recreated successfully with {Count} monitored items.",
                    _initialMonitoredItems.Count);
            }

            await updateBuffer.CompleteInitializationAsync(cancellationToken).ConfigureAwait(false);

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
    /// Writes changes to OPC UA server in chunks.
    /// Returns transient failures for retry, throws on complete failure.
    /// </summary>
    public async ValueTask<SourceWriteResult> WriteToSourceAsync(IReadOnlyList<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var count = changes.Count;
        if (count is 0)
        {
            return SourceWriteResult.Success;
        }

        var session = _sessionManager?.CurrentSession;
        if (session is null || !session.Connected)
        {
            throw new InvalidOperationException("OPC UA session is not connected.");
        }

        var chunkSize = (int)(session.OperationLimits?.MaxNodesPerWrite ?? DefaultChunkSize);
        chunkSize = chunkSize is 0 ? int.MaxValue : chunkSize;

        List<SubjectPropertyChange>? allFailures = null;

        for (var offset = 0; offset < count; offset += chunkSize)
        {
            var take = Math.Min(chunkSize, count - offset);

            var writeValues = CreateWriteValuesCollection(changes, offset, take);
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

                var transientFailures = GetTransientFailedChanges(changes, writeResponse.Results, offset);
                if (transientFailures is not null)
                {
                    allFailures ??= [];
                    allFailures.AddRange(transientFailures);
                }
            }
            catch (Exception ex)
            {
                // Network/communication failure - return remaining changes for retry
                _logger.LogWarning(ex,
                    "OPC UA write communication failed at offset {Offset}, returning {Count} remaining changes for retry.",
                    offset, count - offset);

                allFailures ??= [];
                for (var i = offset; i < count; i++)
                {
                    allFailures.Add(changes[i]);
                }

                return new SourceWriteResult(allFailures);
            }
        }

        return allFailures is { Count: > 0 }
            ? new SourceWriteResult(allFailures)
            : SourceWriteResult.Success;
    }
    
    private WriteValueCollection CreateWriteValuesCollection(IReadOnlyList<SubjectPropertyChange> changes, int offset, int take)
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

    /// <summary>
    /// Collects transient failures for retry and logs summary counts.
    /// </summary>
    private List<SubjectPropertyChange>? GetTransientFailedChanges(
        IReadOnlyList<SubjectPropertyChange> changes,
        StatusCodeCollection results,
        int offset)
    {
        List<SubjectPropertyChange>? transientFailures = null;
        var permanentErrorCount = 0;

        for (var i = 0; i < results.Count; i++)
        {
            if (StatusCode.IsBad(results[i]))
            {
                var change = changes[offset + i];
                var isTransient = IsTransientWriteError(results[i]);

                if (isTransient)
                {
                    transientFailures ??= [];
                    transientFailures.Add(change);
                }
                else
                {
                    permanentErrorCount++;
                }
            }
        }

        if (transientFailures is { Count: > 0 })
        {
            _logger.LogWarning(
                "OPC UA write had {TransientCount} transient failures (will retry).",
                transientFailures.Count);
        }

        if (permanentErrorCount > 0)
        {
            _logger.LogError(
                "OPC UA write had {PermanentCount} permanent failures (not retrying).",
                permanentErrorCount);
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
