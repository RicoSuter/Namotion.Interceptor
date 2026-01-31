using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Namotion.Interceptor.OpcUa.Graph;
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

    private volatile SessionManager? _sessionManager;
    private SubjectPropertyWriter? _propertyWriter;

    private readonly SemaphoreSlim _structureLock = new(1, 1);
    private readonly ConnectorReferenceCounter<List<MonitoredItem>> _subjectRefCounter = new();
    private readonly SubjectTracker _subjectTracker = new();

    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)
    private volatile bool _isStarted;
    private long _reconnectStartedTimestamp; // 0 = not reconnecting, otherwise Stopwatch timestamp when reconnection started (for stall detection)
    private OpcUaClientStructuralChangeProcessor? _structuralChangeProcessor;
    private OpcUaNodeChangeProcessor? _nodeChangeProcessor;

    // Periodic resync timer for servers that don't support ModelChangeEvents
    private Timer? _periodicResyncTimer;
    private volatile bool _periodicResyncInProgress;

    // ModelChangeEvent subscription tracking
    private MonitoredItem? _modelChangeEventItem;

    // Diagnostics tracking - accessed from multiple threads via Diagnostics property
    private long _totalReconnectionAttempts;
    private long _successfulReconnections;
    private long _failedReconnections;
    private long _lastConnectedAtTicks; // 0 = never connected, otherwise UTC ticks (thread-safe via Interlocked)
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

    internal OpcUaSubjectLoader SubjectLoader => _subjectLoader;

    internal OpcUaNodeChangeProcessor? NodeChangeProcessor => _nodeChangeProcessor;

    // Diagnostics accessors
    internal long TotalReconnectionAttempts => Interlocked.Read(ref _totalReconnectionAttempts);
    internal long SuccessfulReconnections => Interlocked.Read(ref _successfulReconnections);
    internal long FailedReconnections => Interlocked.Read(ref _failedReconnections);
    internal DateTimeOffset? LastConnectedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastConnectedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Called by SessionManager when a reconnection attempt starts (via SDK's SessionReconnectHandler).
    /// Updates diagnostics metrics to track the reconnection attempt.
    /// </summary>
    internal void RecordReconnectionAttemptStart()
    {
        Interlocked.Increment(ref _totalReconnectionAttempts);
    }

    /// <summary>
    /// Called by SessionManager when SDK's SessionReconnectHandler successfully reconnects.
    /// Updates diagnostics metrics to track the successful reconnection.
    /// Note: Does not increment TotalReconnectionAttempts - that's done in RecordReconnectionAttemptStart.
    /// </summary>
    private void RecordReconnectionSuccess()
    {
        Interlocked.Increment(ref _successfulReconnections);
        Interlocked.Exchange(ref _lastConnectedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    private void RemoveItemsForSubject(IInterceptorSubject subject)
    {
        _structureLock.Wait();
        NodeId? nodeIdToDelete = null;
        try
        {
            var isLast = _subjectRefCounter.DecrementAndCheckLast(subject, out _);

            if (isLast)
            {
                _subjectTracker.UntrackSubject(subject, out nodeIdToDelete);

                _sessionManager?.SubscriptionManager.RemoveItemsForSubject(subject);
                _sessionManager?.PollingManager?.RemoveItemsForSubject(subject);
            }
        }
        finally
        {
            _structureLock.Release();
        }

        // Delete remote node if enabled
        if (nodeIdToDelete is not null && _configuration.EnableRemoteNodeManagement)
        {
            _nodeChangeProcessor?.MarkRecentlyDeleted(nodeIdToDelete);

            var session = _sessionManager?.CurrentSession;
            if (session is not null && session.Connected)
            {
                _ = TryDeleteRemoteNodeAsync(session, nodeIdToDelete, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Attempts to delete a remote node on the OPC UA server via DeleteNodes service.
    /// </summary>
    private async Task TryDeleteRemoteNodeAsync(ISession session, NodeId nodeId, CancellationToken cancellationToken)
    {
        var deleteNodesItem = new DeleteNodesItem
        {
            NodeId = nodeId,
            DeleteTargetReferences = true
        };

        var nodesToDelete = new DeleteNodesItemCollection { deleteNodesItem };

        try
        {
            var response = await session.DeleteNodesAsync(
                null,
                nodesToDelete,
                cancellationToken).ConfigureAwait(false);

            if (response.Results.Count > 0)
            {
                var result = response.Results[0];
                if (StatusCode.IsGood(result))
                {
                    _logger.LogDebug(
                        "Deleted remote node '{NodeId}'.",
                        nodeId);
                }
                else
                {
                    _logger.LogWarning(
                        "DeleteNodes failed for '{NodeId}': {StatusCode}.",
                        nodeId, result);
                }
            }
        }
        catch (ServiceResultException ex)
        {
            _logger.LogWarning(ex,
                "DeleteNodes service call failed for '{NodeId}'.",
                nodeId);
        }
    }

    /// <summary>
    /// Tracks a subject with its associated monitored items and NodeId.
    /// Uses reference counting - only creates monitored items on first reference.
    /// </summary>
    /// <param name="subject">The subject to track.</param>
    /// <param name="nodeId">The OPC UA NodeId for this subject.</param>
    /// <param name="monitoredItemsFactory">Factory to create monitored items on first reference.</param>
    /// <returns>True if this is the first reference (caller should create subscriptions), false otherwise.</returns>
    internal bool TrackSubject(IInterceptorSubject subject, NodeId nodeId, Func<List<MonitoredItem>> monitoredItemsFactory)
    {
        var isFirst = _subjectRefCounter.IncrementAndCheckFirst(subject, monitoredItemsFactory, out _);
        if (isFirst)
        {
            _subjectTracker.TrackSubject(subject, nodeId);
        }
        return isFirst;
    }

    /// <summary>
    /// Gets the NodeId for a tracked subject.
    /// </summary>
    /// <param name="subject">The subject to look up.</param>
    /// <param name="nodeId">The NodeId if found.</param>
    /// <returns>True if the subject is tracked, false otherwise.</returns>
    internal bool TryGetSubjectNodeId(IInterceptorSubject subject, out NodeId? nodeId)
    {
        return _subjectTracker.TryGetNodeId(subject, out nodeId);
    }

    /// <summary>
    /// Gets the monitored items for a tracked subject.
    /// </summary>
    /// <param name="subject">The subject to look up.</param>
    /// <param name="monitoredItems">The monitored items if found.</param>
    /// <returns>True if the subject is tracked, false otherwise.</returns>
    internal bool TryGetSubjectMonitoredItems(IInterceptorSubject subject, out List<MonitoredItem>? monitoredItems)
    {
        return _subjectRefCounter.TryGetData(subject, out monitoredItems);
    }

    /// <summary>
    /// Adds a monitored item to a subject's tracked list.
    /// </summary>
    /// <param name="subject">The subject that owns this monitored item.</param>
    /// <param name="monitoredItem">The monitored item to add.</param>
    internal void AddMonitoredItemToSubject(IInterceptorSubject subject, MonitoredItem monitoredItem)
    {
        if (_subjectRefCounter.TryGetData(subject, out var monitoredItems) && monitoredItems is not null)
        {
            monitoredItems.Add(monitoredItem);
        }
    }

    /// <summary>
    /// Checks if a subject is already tracked by the reference counter.
    /// </summary>
    /// <param name="subject">The subject to check.</param>
    /// <returns>True if the subject is tracked, false otherwise.</returns>
    internal bool IsSubjectTracked(IInterceptorSubject subject)
    {
        return _subjectRefCounter.TryGetData(subject, out _);
    }

    /// <summary>
    /// Gets all tracked subjects.
    /// </summary>
    /// <returns>All tracked subjects.</returns>
    internal IEnumerable<IInterceptorSubject> GetTrackedSubjects()
    {
        return _subjectTracker.GetAllSubjects();
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
            onReleasing: property =>
            {
                // Unregister from ReadAfterWriteManager FIRST (uses NodeId)
                if (property.TryGetPropertyData(OpcUaNodeIdKey, out var nodeIdObj) &&
                    nodeIdObj is NodeId nodeId)
                {
                    _sessionManager?.ReadAfterWriteManager?.UnregisterProperty(nodeId);
                }
                property.RemovePropertyData(OpcUaNodeIdKey);
            },
            onSubjectDetaching: OnSubjectDetaching);
        _subjectLoader = new OpcUaSubjectLoader(configuration, _ownership, this, logger);
        _subscriptionHealthMonitor = new SubscriptionHealthMonitor(logger);
    }

    private void OnSubjectDetaching(IInterceptorSubject subject)
    {
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

        Interlocked.Exchange(ref _lastConnectedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
        _logger.LogInformation("Connected to OPC UA server successfully.");

        await _structureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        }
        finally
        {
            _structureLock.Release();
        }

        // Create structural change processor if live sync is enabled
        if (_configuration.EnableLiveSync)
        {
            _structuralChangeProcessor = new OpcUaClientStructuralChangeProcessor(
                this,
                _configuration,
                _subjectLoader,
                _logger);
        }

        // Create node change processor for remote sync features
        if (_configuration.EnableModelChangeEvents || _configuration.EnablePeriodicResync)
        {
            _nodeChangeProcessor = new OpcUaNodeChangeProcessor(
                this,
                _configuration,
                _subjectLoader,
                _logger);

            // Register all tracked subjects with the processor for reverse NodeId lookup
            foreach (var subject in GetTrackedSubjects())
            {
                if (TryGetSubjectNodeId(subject, out var nodeId) && nodeId is not null)
                {
                    _nodeChangeProcessor.RegisterSubjectNodeId(subject, nodeId);
                }
            }
        }

        // Subscribe to ModelChangeEvents if enabled
        if (_configuration.EnableModelChangeEvents)
        {
            await SetupModelChangeEventSubscriptionAsync(session, cancellationToken).ConfigureAwait(false);
        }

        // Start periodic resync timer if enabled
        if (_configuration.EnablePeriodicResync)
        {
            StartPeriodicResyncTimer();
        }

        _isStarted = true;
        return _sessionManager;
    }

    private async Task SetupModelChangeEventSubscriptionAsync(Session session, CancellationToken cancellationToken)
    {
        try
        {
            // Create an event monitored item for GeneralModelChangeEventType on the Server node
            var eventFilter = new EventFilter();
            eventFilter.SelectClauses.Add(new SimpleAttributeOperand
            {
                TypeDefinitionId = ObjectTypeIds.GeneralModelChangeEventType,
                BrowsePath = { new QualifiedName("Changes") },
                AttributeId = Opc.Ua.Attributes.Value
            });
            eventFilter.SelectClauses.Add(new SimpleAttributeOperand
            {
                TypeDefinitionId = ObjectTypeIds.BaseEventType,
                BrowsePath = { new QualifiedName("EventType") },
                AttributeId = Opc.Ua.Attributes.Value
            });

            _modelChangeEventItem = new MonitoredItem(_configuration.TelemetryContext)
            {
                DisplayName = "ModelChangeEvent",
                StartNodeId = ObjectIds.Server,
                AttributeId = Opc.Ua.Attributes.EventNotifier,
                SamplingInterval = 0,
                QueueSize = 100,
                DiscardOldest = true,
                Filter = eventFilter
            };

            // Add to first subscription or create new one
            var sessionManager = _sessionManager;
            if (sessionManager is not null)
            {
                // Subscribe to event notifications via FastEventCallback (since DisableMonitoredItemCache=true)
                sessionManager.SubscriptionManager.EventNotificationReceived += OnEventNotificationReceived;

                await sessionManager.AddMonitoredItemsAsync([_modelChangeEventItem], session, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Subscribed to ModelChangeEvents on Server node. Status={Status}, MonitoringMode={Mode}",
                    _modelChangeEventItem.Status?.Id.ToString() ?? "null",
                    _modelChangeEventItem.MonitoringMode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to ModelChangeEvents. Server may not support GeneralModelChangeEventType.");
            _modelChangeEventItem = null;
        }
    }

    private void OnEventNotificationReceived(EventNotificationList notification)
    {
        // Dispatch event notifications to the appropriate handler based on ClientHandle
        var modelChangeItem = _modelChangeEventItem;
        if (modelChangeItem is null)
        {
            return;
        }

        foreach (var eventFields in notification.Events)
        {
            if (eventFields.ClientHandle == modelChangeItem.ClientHandle)
            {
                OnModelChangeEventNotification(eventFields);
            }
        }
    }

    private void OnModelChangeEventNotification(EventFieldList eventFields)
    {
        if (_nodeChangeProcessor is null)
        {
            return;
        }

        var session = _sessionManager?.CurrentSession;
        if (session is null || !session.Connected)
        {
            return;
        }

        _logger.LogDebug("ModelChangeEvent received with {Count} event fields.", eventFields.EventFields.Count);

        // First field should be the Changes array (ModelChangeStructureDataType[])
        if (eventFields.EventFields.Count > 0 && eventFields.EventFields[0].Value is ExtensionObject[] changes)
        {
            var modelChanges = new List<ModelChangeStructureDataType>();
            foreach (var change in changes)
            {
                if (change.Body is ModelChangeStructureDataType modelChange)
                {
                    modelChanges.Add(modelChange);
                }
            }

            if (modelChanges.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _nodeChangeProcessor.ProcessModelChangeEventAsync(modelChanges, session, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing ModelChangeEvent.");
                    }
                });
            }
        }
    }

    private void StartPeriodicResyncTimer()
    {
        var interval = _configuration.PeriodicResyncInterval;
        _periodicResyncTimer = new Timer(OnPeriodicResyncTimerCallback, null, interval, interval);
    }

    private void OnPeriodicResyncTimerCallback(object? state)
    {
        if (Volatile.Read(ref _disposed) == 1 ||
            _periodicResyncInProgress ||
            !_isStarted)
        {
            return;
        }

        var session = _sessionManager?.CurrentSession;
        if (session is null || !session.Connected)
        {
            return;
        }

        var processor = _nodeChangeProcessor;
        if (processor is null)
        {
            _logger.LogWarning("Periodic resync skipped: _nodeChangeProcessor is null.");
            return;
        }

        _logger.LogInformation("Starting periodic resync.");
        _periodicResyncInProgress = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await processor.PerformFullResyncAsync(session, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during periodic resync.");
            }
            finally
            {
                _periodicResyncInProgress = false;
            }
        });
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

        return () =>
        {
            foreach (var (property, dataValue) in result)
            {
                var value = _configuration.ValueConverter.ConvertToPropertyValue(dataValue.Value, property);
                property.SetValueFromSource(this, dataValue.SourceTimestamp, null, value);
            }
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
    /// Reads and applies initial values for a newly created subject's properties.
    /// Called after dynamically creating a subject (e.g., for reference properties or collection items).
    /// </summary>
    /// <param name="subject">The subject whose properties should be read.</param>
    /// <param name="session">The OPC UA session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task ReadAndApplySubjectValuesAsync(IInterceptorSubject subject, ISession session, CancellationToken cancellationToken)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            _logger.LogWarning("Cannot read values: subject {Type} is not registered.", subject.GetType().Name);
            return;
        }

        // Collect properties with NodeIds for this subject
        var propertiesWithNodeIds = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        foreach (var property in registeredSubject.Properties)
        {
            if (property.Reference.TryGetPropertyData(OpcUaNodeIdKey, out var nodeIdObj) &&
                nodeIdObj is NodeId nodeId)
            {
                propertiesWithNodeIds.Add((property, nodeId));
            }
        }

        if (propertiesWithNodeIds.Count == 0)
        {
            return;
        }

        var readValues = new ReadValueIdCollection(propertiesWithNodeIds.Count);
        foreach (var (_, nodeId) in propertiesWithNodeIds)
        {
            readValues.Add(new ReadValueId
            {
                NodeId = nodeId,
                AttributeId = Opc.Ua.Attributes.Value
            });
        }

        var readResponse = await session.ReadAsync(
            requestHeader: null,
            maxAge: 0,
            timestampsToReturn: TimestampsToReturn.Source,
            readValues,
            cancellationToken).ConfigureAwait(false);

        var resultCount = Math.Min(readResponse.Results.Count, propertiesWithNodeIds.Count);
        for (var i = 0; i < resultCount; i++)
        {
            if (StatusCode.IsGood(readResponse.Results[i].StatusCode))
            {
                var dataValue = readResponse.Results[i];
                var property = propertiesWithNodeIds[i].Property;
                var value = _configuration.ValueConverter.ConvertToPropertyValue(dataValue.Value, property);
                property.SetValueFromSource(this, dataValue.SourceTimestamp, null, value);
            }
        }
    }

    /// <summary>
    /// Creates MonitoredItems for reconnection from owned properties.
    /// This recreates SDK objects on demand instead of holding them in memory.
    /// Called by SessionManager when subscription transfer fails after server restart.
    /// </summary>
    private IReadOnlyList<MonitoredItem> CreateMonitoredItemsForReconnection()
    {
        var ownedProperties = GetOwnedPropertiesWithNodeIds();
        var monitoredItems = new List<MonitoredItem>(ownedProperties.Count);

        foreach (var (property, nodeId) in ownedProperties)
        {
            var monitoredItem = MonitoredItemFactory.Create(_configuration, nodeId, property);
            monitoredItems.Add(monitoredItem);
        }

        return monitoredItems;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Single-threaded health check loop. Coordinates with automatic reconnection via IsReconnecting flag.
        // All async work from SDK reconnection callbacks is deferred to this loop for simplicity.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sessionManager = _sessionManager; // Capture reference to avoid TOCTOU
                var propertyWriter = _propertyWriter;
                if (sessionManager is not null && propertyWriter is not null && _isStarted)
                {
                    // 1. Cleanup pending old session from SDK reconnection
                    if (sessionManager.PendingOldSession is not null)
                    {
                        await sessionManager.DisposePendingOldSessionAsync(stoppingToken).ConfigureAwait(false);
                    }

                    // 2. Complete initialization after SDK reconnection with subscription transfer
                    if (sessionManager.NeedsInitialization)
                    {
                        try
                        {
                            await propertyWriter.CompleteInitializationAsync(stoppingToken).ConfigureAwait(false);
                            sessionManager.ClearInitializationFlag();
                            RecordReconnectionSuccess();
                            _logger.LogInformation("SDK reconnection initialization completed successfully.");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "SDK reconnection initialization failed. Clearing session for full restart.");
                            sessionManager.ClearInitializationFlag();
                            await sessionManager.ClearSessionAsync(stoppingToken).ConfigureAwait(false);
                        }
                    }

                    // 3. Check session health and trigger reconnection if needed
                    var isReconnecting = sessionManager.IsReconnecting;
                    var currentSession = sessionManager.CurrentSession;
                    var sessionIsConnected = currentSession?.Connected ?? false;

                    if (currentSession is not null && sessionIsConnected && !isReconnecting)
                    {
                        // Session healthy - validate subscriptions and reset stall detection timestamp
                        Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);
                        await _subscriptionHealthMonitor.CheckAndHealSubscriptionsAsync(
                            sessionManager.Subscriptions,
                            stoppingToken).ConfigureAwait(false);
                    }
                    else if (!isReconnecting && (currentSession is null || !sessionIsConnected))
                    {
                        // Session is dead and no reconnection in progress
                        Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);
                        _logger.LogWarning(
                            "OPC UA session is dead (session={HasSession}, connected={IsConnected}). " +
                            "Starting manual reconnection...",
                            currentSession is not null,
                            sessionIsConnected);

                        await ReconnectSessionAsync(stoppingToken).ConfigureAwait(false);
                    }
                    else if (isReconnecting)
                    {
                        // SDK reconnection in progress - check for stall using time-based detection
                        // Note: We check stall regardless of session.Connected state because the old
                        // session's Connected property can return stale values during SDK reconnection.
                        // Uses Stopwatch for monotonic timing (immune to clock drift/jumps).
                        var startedAt = Interlocked.Read(ref _reconnectStartedTimestamp);
                        if (startedAt == 0)
                        {
                            // First detection of reconnecting state - record start time
                            Interlocked.CompareExchange(ref _reconnectStartedTimestamp, Stopwatch.GetTimestamp(), 0);
                        }
                        else
                        {
                            var elapsed = Stopwatch.GetElapsedTime(startedAt);
                            if (elapsed > _configuration.MaxReconnectDuration)
                            {
                                // SDK handler likely timed out or is stuck - force reset and trigger manual reconnection
                                if (sessionManager.TryForceResetIfStalled())
                                {
                                    _logger.LogWarning(
                                        "SDK reconnection stalled (session={HasSession}, connected={IsConnected}, elapsed={Elapsed}s). " +
                                        "Starting manual reconnection...",
                                        currentSession is not null,
                                        sessionIsConnected,
                                        elapsed.TotalSeconds);

                                    Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);
                                    await ReconnectSessionAsync(stoppingToken).ConfigureAwait(false);
                                }
                            }
                        }
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
                _logger.LogError(ex, "Error during health check or session restart. Retrying after delay.");
                await Task.Delay(_configuration.SubscriptionHealthCheckInterval, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("OPC UA client has stopped.");
    }

    /// <summary>
    /// Restarts the session when connection is lost.
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

        try
        {
            _logger.LogInformation("Restarting OPC UA session...");

            // Start collecting updates - any incoming subscription notifications will be buffered
            // until we complete the full state reload
            propertyWriter.StartBuffering();

            // Create new session (CreateSessionAsync disposes old session internally)
            var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);
            var session = await sessionManager.CreateSessionAsync(application, _configuration, cancellationToken).ConfigureAwait(false);

            // Clear all read-after-write state - new session means old pending reads and registrations are invalid
            sessionManager.ReadAfterWriteManager?.ClearAll();

            _logger.LogInformation("New OPC UA session created successfully.");

            await _structureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Recreate MonitoredItems from owned properties (avoids memory leak from holding SDK objects)
                var monitoredItems = CreateMonitoredItemsForReconnection();
                if (monitoredItems.Count > 0)
                {
                    await sessionManager.CreateSubscriptionsAsync(monitoredItems, session, cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Subscriptions recreated successfully with {Count} monitored items.",
                        monitoredItems.Count);
                }
            }
            finally
            {
                _structureLock.Release();
            }

            await propertyWriter.CompleteInitializationAsync(cancellationToken).ConfigureAwait(false);

            Interlocked.Increment(ref _successfulReconnections);
            Interlocked.Exchange(ref _lastConnectedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
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
            var references = await OpcUaBrowseHelper.BrowseNodeAsync(session, ObjectIds.ObjectsFolder, cancellationToken).ConfigureAwait(false);
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

            // Process structural changes first if live sync is enabled
            var structuralProcessor = _structuralChangeProcessor;
            if (structuralProcessor is not null)
            {
                structuralProcessor.CurrentSession = session;

                for (var i = 0; i < changes.Length; i++)
                {
                    var change = changes.Span[i];
                    var registeredProperty = change.Property.TryGetRegisteredProperty();
                    if (registeredProperty is null)
                    {
                        continue;
                    }

                    // Check if this is a structural change and process it
                    // Structural properties (IsSubjectReference, IsSubjectCollection, IsSubjectDictionary)
                    // are fully handled here - they don't have NodeIds so CreateWriteValuesCollection will skip them
                    await structuralProcessor
                        .ProcessPropertyChangeAsync(change, registeredProperty)
                        .ConfigureAwait(false);
                }
            }

            var writeValues = CreateWriteValuesCollection(changes);
            if (writeValues.Count is 0)
            {
                return WriteResult.Success;
            }

            var writeResponse = await session.WriteAsync(requestHeader: null, writeValues, cancellationToken).ConfigureAwait(false);
            var result = ProcessWriteResults(writeResponse.Results, changes);
            if (result.IsFullySuccessful || result.IsPartialFailure)
            {
                NotifyPropertiesWritten(changes);
            }

            return result;
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
            if (!TryGetWritableNodeId(change, out _, out _))
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

    private bool TryGetWritableNodeId(SubjectPropertyChange change, out NodeId nodeId, out RegisteredSubjectProperty registeredProperty)
    {
        nodeId = null!;
        registeredProperty = null!;

        if (!change.Property.TryGetPropertyData(OpcUaNodeIdKey, out var value) || value is not NodeId id)
        {
            return false;
        }

        if (change.Property.TryGetRegisteredProperty() is not { HasSetter: true } property)
        {
            return false;
        }

        nodeId = id;
        registeredProperty = property;
        return true;
    }

    private WriteValueCollection CreateWriteValuesCollection(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var span = changes.Span;
        var writeValues = new WriteValueCollection(span.Length);

        for (var i = 0; i < span.Length; i++)
        {
            var change = span[i];

            if (!TryGetWritableNodeId(change, out var nodeId, out var registeredProperty))
            {
                continue;
            }

            var convertedValue = _configuration.ValueConverter.ConvertToNodeValue(
                change.GetNewValue<object?>(),
                registeredProperty);

            writeValues.Add(new WriteValue
            {
                NodeId = nodeId,
                AttributeId = Opc.Ua.Attributes.Value,
                Value = new DataValue
                {
                    Value = convertedValue,
                    StatusCode = StatusCodes.Good,
                    SourceTimestamp = change.ChangedTimestamp.UtcDateTime
                }
            });
        }

        return writeValues;
    }

    /// <summary>
    /// Notifies ReadAfterWriteManager that properties were written.
    /// Schedules read-after-writes for properties where SamplingInterval=0 was revised to non-zero.
    /// </summary>
    private void NotifyPropertiesWritten(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var manager = _sessionManager?.ReadAfterWriteManager;
        if (manager is null)
        {
            return;
        }

        var span = changes.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i].Property.TryGetPropertyData(OpcUaNodeIdKey, out var nodeIdObj) &&
                nodeIdObj is NodeId nodeId)
            {
                manager.OnPropertyWritten(nodeId);
            }
        }
    }

    /// <summary>
    /// Determines if a write failure status code represents a transient error that should be retried.
    /// Returns true for transient errors (connectivity, timeouts, server load), false for permanent errors.
    /// </summary>
    internal static bool IsTransientWriteError(StatusCode statusCode)
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

    private void Reset()
    {
        _isStarted = false;
        _structuralChangeProcessor = null;

        // Stop and dispose periodic resync timer
        _periodicResyncTimer?.Dispose();
        _periodicResyncTimer = null;
        _periodicResyncInProgress = false;

        // Clean up ModelChangeEvent subscription
        var sessionManager = _sessionManager;
        if (sessionManager is not null)
        {
            sessionManager.SubscriptionManager.EventNotificationReceived -= OnEventNotificationReceived;
        }
        _modelChangeEventItem = null;

        // Clear node change processor
        _nodeChangeProcessor?.Clear();
        _nodeChangeProcessor = null;

        CleanupPropertyData();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // Already disposed
        }

        // Stop and dispose periodic resync timer first
        _periodicResyncTimer?.Dispose();
        _periodicResyncTimer = null;

        // Clean up ModelChangeEvent subscription and dispose session manager
        var sessionManager = _sessionManager;
        if (sessionManager is not null)
        {
            sessionManager.SubscriptionManager.EventNotificationReceived -= OnEventNotificationReceived;
            await sessionManager.DisposeAsync().ConfigureAwait(false);
            _sessionManager = null;
        }
        _modelChangeEventItem = null;

        // Clear node change processor
        _nodeChangeProcessor?.Clear();
        _nodeChangeProcessor = null;

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

        // Clear reference counter and NodeId mapping for fresh resync
        _subjectRefCounter.Clear();
        _subjectTracker.Clear();
    }
}
