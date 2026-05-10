using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

internal sealed class OpcUaSubjectClientSource : SubjectSourceBase, IOpcUaSubjectClientSource, IFaultInjectable, IAsyncDisposable
{
    private const int DefaultChunkSize = 512;

    /// <summary>
    /// Well-known key for storing the OPC UA NodeId in a subject's Data dictionary.
    /// Used by the reconciler and structure handler to look up which NodeId a subject represents.
    /// </summary>
    internal const string SubjectNodeIdDataKey = "OpcUa:SubjectNodeId";

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;

    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly SubscriptionHealthMonitor _subscriptionHealthMonitor;

    private volatile SessionManager? _sessionManager;
    private volatile SubjectPropertyWriter? _propertyWriter;
    private OutboundWriter? _writer;
    private OpcUaClientIncomingEventProcessor? _incomingProcessor;
    private Task? _incomingProcessorTask;
    private OpcUaModelChangeEventHandler? _modelChangeHandler;

    private readonly SemaphoreSlim _structureLock = new(1, 1);
    private volatile CancellationTokenSource? _reconnectCts;
    private int _disposed;

    private volatile bool _isStarted;
    private long _reconnectStartedTimestamp; // 0 = not reconnecting, otherwise Stopwatch timestamp when reconnection started (for stall detection)
    private Exception? _lastError;

    internal string OpcUaNodeIdKey { get; } = "OpcUaNodeId:" + Guid.NewGuid();

    internal SessionManager? SessionManager => _sessionManager;
    internal SourceOwnershipManager Ownership => _ownership;

    internal ReconnectionMetrics ReconnectionMetrics { get; } = new();
    internal ThroughputCounter IncomingThroughput { get; } = new();
    internal ThroughputCounter OutgoingThroughput { get; } = new();
    internal Exception? LastError => Volatile.Read(ref _lastError);

    internal void ClearLastError() => Volatile.Write(ref _lastError, null);

    /// <inheritdoc />
    public override int WriteBatchSize => _writer?.WriteBatchSize ?? 0;

    /// <inheritdoc />
    public OpcUaClientDiagnostics Diagnostics { get; }

    /// <inheritdoc />
    public ISession? CurrentSession => _sessionManager?.CurrentSession;

    /// <inheritdoc />
    public event EventHandler<OpcUaCurrentSessionChangedEventArgs>? CurrentSessionChanged;

    public OpcUaSubjectClientSource(IInterceptorSubject subject, OpcUaClientConfiguration configuration, ILogger logger)
        : base(subject.Context, logger, configuration.BufferTime, configuration.RetryTime, configuration.WriteRetryQueueSize)
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

        Diagnostics = new OpcUaClientDiagnostics(this);
    }

    private void OnSubjectDetaching(IInterceptorSubject subject)
    {
        RemoveItemsForSubject(subject);

        if (_incomingProcessor is not null &&
            subject.TryGetData(SubjectNodeIdDataKey, out var nodeIdObj) &&
            nodeIdObj is NodeId nodeId)
        {
            _incomingProcessor.RemoveEcho(nodeId);
        }
    }

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject => _subject;

    protected override async Task<IAsyncDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        Reset();

        _propertyWriter = propertyWriter;
        _logger.LogInformation("Connecting to OPC UA server at {ServerUrl}.", _configuration.ServerUrl);

        _sessionManager = new SessionManager(this, propertyWriter, _configuration, _logger);
        _writer = new OutboundWriter(_sessionManager, _configuration, OpcUaNodeIdKey, OutgoingThroughput, _logger);

        try
        {
            var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);
            await _sessionManager.CreateSessionAsync(application, _configuration, cancellationToken).ConfigureAwait(false);

            ReconnectionMetrics.RecordInitialConnection();
            _logger.LogInformation("Connected to OPC UA server successfully.");

            _isStarted = true;
            Volatile.Write(ref _lastError, null);

            var sessionManagerForLifetime = _sessionManager;
            return BackgroundTaskLifetime.Start(
                cancellationToken,
                _logger,
                RunHealthCheckLoopAsync,
                async () =>
                {
                    try { await sessionManagerForLifetime.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogWarning(ex, "OPC UA session manager threw during listen-lifetime disposal."); }
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupSessionManagerAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastError, ex);
            await CleanupSessionManagerAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task CleanupSessionManagerAsync()
    {
        var sessionManager = _sessionManager;
        if (sessionManager is not null)
        {
            try { await sessionManager.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "OPC UA session manager threw during listen-failure cleanup."); }
            _sessionManager = null;
        }
    }

    /// <inheritdoc />
    public override async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        var sessionManager = _sessionManager;
        var session = sessionManager?.CurrentSession;
        if (session is null || sessionManager is null)
        {
            throw new InvalidOperationException("No active OPC UA session available.");
        }

        await _structureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rootNode = await TryGetRootNodeAsync((Session)session, cancellationToken).ConfigureAwait(false);
            if (rootNode is not null)
            {
                var monitoredItems = await _subjectLoader.LoadSubjectAsync(_subject, rootNode, session, cancellationToken).ConfigureAwait(false);
                if (monitoredItems.Count > 0)
                {
                    await sessionManager.CreateSubscriptionsAsync(monitoredItems, (Session)session, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning("No OPC UA monitored items found.");
                }

                if (_configuration.EnableStructureSynchronization)
                {
                    _incomingProcessor = new OpcUaClientIncomingEventProcessor(
                        _subjectLoader, this, _configuration, _logger);
                    _incomingProcessor.SetSession((Session)session, sessionManager.SubscriptionManager);
                    _incomingProcessor.PopulateSubjectMap(_subject);
                    sessionManager.SubscriptionManager.SetIncomingEventProcessor(_incomingProcessor);

                    _modelChangeHandler = new OpcUaModelChangeEventHandler(
                        (verb, nodeId, _) =>
                        {
                            var eventType = verb == ModelChangeStructureVerbMask.NodeAdded
                                ? IncomingEventType.StructuralAdd
                                : IncomingEventType.StructuralRemove;
                            _incomingProcessor.Enqueue(new IncomingEvent
                            {
                                Type = eventType,
                                AffectedNodeId = nodeId
                            });
                            return Task.CompletedTask;
                        },
                        _logger);
                    await _modelChangeHandler.SubscribeAsync((Session)session, cancellationToken).ConfigureAwait(false);

                    _incomingProcessorTask = _incomingProcessor.RunAsync(cancellationToken);
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

        var ownedProperties = GetOwnedPropertiesWithNodeIds();
        if (ownedProperties.Count == 0)
        {
            return null;
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

    /// <inheritdoc />
    public bool TryGetNodeId(PropertyReference property, [NotNullWhen(true)] out NodeId? nodeId)
    {
        if (property.TryGetPropertyData(OpcUaNodeIdKey, out var data) && data is NodeId resolved)
        {
            nodeId = resolved;
            return true;
        }

        nodeId = null;
        return false;
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

    private async Task RunHealthCheckLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sessionManager = _sessionManager;
                var propertyWriter = _propertyWriter;
                if (sessionManager is not null && propertyWriter is not null && _isStarted)
                {
                    if (sessionManager.HasSessionsToDispose)
                    {
                        await sessionManager.DisposePendingSessionsAsync(stoppingToken).ConfigureAwait(false);
                    }

                    var isReconnecting = sessionManager.IsReconnecting;
                    var currentSession = sessionManager.CurrentSession;
                    var sessionIsConnected = currentSession?.Connected ?? false;

                    if (currentSession is not null && sessionIsConnected && !isReconnecting)
                    {
                        if (await HandleHealthySessionAsync(sessionManager, stoppingToken).ConfigureAwait(false))
                        {
                            continue;
                        }
                    }
                    else if (!isReconnecting)
                    {
                        await HandleDeadSessionAsync(currentSession, sessionIsConnected, stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await HandleReconnectionStallDetectionAsync(sessionManager, currentSession, sessionIsConnected, stoppingToken).ConfigureAwait(false);
                    }
                }

                await Task.Delay(_configuration.SubscriptionHealthCheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check or session restart. Retrying after delay.");
                try { await Task.Delay(_configuration.SubscriptionHealthCheckInterval, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        _logger.LogInformation("OPC UA client health loop has stopped.");
    }

    private async Task<bool> HandleHealthySessionAsync(SessionManager sessionManager, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);

        await sessionManager.PerformFullStateSyncIfNeededAsync(cancellationToken).ConfigureAwait(false);

        if (sessionManager.SubscriptionManager.HasStoppedPublishing)
        {
            _logger.LogWarning(
                "OPC UA subscription has stopped publishing. Starting manual reconnection to recover notification flow...");
            await ReconnectSessionAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        await _subscriptionHealthMonitor.CheckAndHealSubscriptionsAsync(
            sessionManager.Subscriptions,
            cancellationToken).ConfigureAwait(false);

        return false;
    }

    private async Task HandleDeadSessionAsync(Session? currentSession, bool sessionIsConnected, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);
        _logger.LogWarning(
            "OPC UA session is dead (session={HasSession}, connected={IsConnected}). " +
            "Starting manual reconnection...",
            currentSession is not null,
            sessionIsConnected);

        await ReconnectSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleReconnectionStallDetectionAsync(SessionManager sessionManager, Session? currentSession, bool sessionIsConnected, CancellationToken cancellationToken)
    {
        var startedAt = Interlocked.Read(ref _reconnectStartedTimestamp);
        if (startedAt == 0)
        {
            Interlocked.CompareExchange(ref _reconnectStartedTimestamp, Stopwatch.GetTimestamp(), 0);
        }
        else
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (elapsed > _configuration.MaxReconnectDuration)
            {
                if (sessionManager.TryForceResetIfStalled())
                {
                    _logger.LogWarning(
                        "SDK reconnection stalled (session={HasSession}, connected={IsConnected}, elapsed={Elapsed}s). " +
                        "Starting manual reconnection...",
                        currentSession is not null,
                        sessionIsConnected,
                        elapsed.TotalSeconds);

                    Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);
                    await ReconnectSessionAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
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

        ReconnectionMetrics.RecordAttemptStart();

        // Create a linked CTS so KillAsync can cancel this reconnection mid-flight.
        // Without this, KillAsync disposes the session while we're still using it,
        // leading to BadSessionNotActivated errors and 60s hangs.
        using var reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _reconnectCts = reconnectCts;

        // Prevent OnKeepAlive from triggering SDK reconnection during manual reconnection.
        // Without this, keep-alive on the newly created session can fire immediately,
        // triggering OnKeepAlive → BeginReconnect → OnReconnectComplete → AbandonCurrentSession,
        // which nullifies the session while we're still setting up subscriptions and loading state.
        sessionManager.SetReconnecting(true);

        try
        {
            var token = reconnectCts.Token;
            _logger.LogInformation("Restarting OPC UA session...");

            // Start collecting updates - any incoming subscription notifications will be buffered
            // until we complete the full state reload
            propertyWriter.StartBuffering();

            // Create new session (CreateSessionAsync disposes old session internally)
            var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);
            var session = await sessionManager.CreateSessionAsync(application, _configuration, token).ConfigureAwait(false);

            // Clear all read-after-write state - new session means old pending reads and registrations are invalid
            sessionManager.ReadAfterWriteManager?.ClearAll();
            _incomingProcessor?.ClearEchoes();

            _logger.LogInformation(
                "New OPC UA session created successfully (id={SessionId}, connected={Connected}).",
                session.SessionId,
                session.Connected);

            await _structureLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Recreate MonitoredItems from owned properties (avoids memory leak from holding SDK objects)
                var monitoredItems = CreateMonitoredItemsForReconnection();
                if (monitoredItems.Count > 0)
                {
                    await sessionManager.CreateSubscriptionsAsync(monitoredItems, session, token).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Subscriptions recreated successfully with {Count} monitored items.",
                        monitoredItems.Count);
                }
            }
            finally
            {
                _structureLock.Release();
            }

            if (_incomingProcessor is not null)
            {
                _incomingProcessor.SetSession(session, sessionManager.SubscriptionManager);
                sessionManager.SubscriptionManager.SetIncomingEventProcessor(_incomingProcessor);

                if (_modelChangeHandler is not null)
                {
                    await _modelChangeHandler.DisposeAsync().ConfigureAwait(false);
                }

                _modelChangeHandler = new OpcUaModelChangeEventHandler(
                    (verb, nodeId, _) =>
                    {
                        var eventType = verb == ModelChangeStructureVerbMask.NodeAdded
                            ? IncomingEventType.StructuralAdd
                            : IncomingEventType.StructuralRemove;
                        _incomingProcessor.Enqueue(new IncomingEvent
                        {
                            Type = eventType,
                            AffectedNodeId = nodeId
                        });
                        return Task.CompletedTask;
                    },
                    _logger);
                await _modelChangeHandler.SubscribeAsync(session, token).ConfigureAwait(false);
            }

            await propertyWriter.LoadInitialStateAndResumeAsync(token).ConfigureAwait(false);

            ReconnectionMetrics.RecordSuccess();
            Volatile.Write(ref _lastError, null);
            _logger.LogInformation("Session restart complete (id={SessionId}).", session.SessionId);
        }
        catch (OperationCanceledException) when (reconnectCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            ReconnectionMetrics.RecordAbandoned();
            _logger.LogInformation("Reconnection cancelled by kill. Will retry on next health check.");

            // Clear the session so health check can trigger a fresh reconnection attempt
            await sessionManager.ClearSessionAsync(cancellationToken).ConfigureAwait(false);

            throw; // Re-throw to trigger retry in ExecuteAsync
        }
        catch (Exception ex)
        {
            ReconnectionMetrics.RecordFailure();
            Volatile.Write(ref _lastError, ex);
            _logger.LogError(ex, "Failed to restart session. Will retry on next health check.");

            // Clear the session so health check can trigger a new reconnection attempt
            await sessionManager.ClearSessionAsync(cancellationToken).ConfigureAwait(false);

            throw; // Re-throw to trigger retry in ExecuteAsync
        }
        finally
        {
            // Always clear the reconnecting flag when manual reconnection completes.
            // On failure paths, ClearSessionAsync already resets this, but the explicit
            // reset ensures it's always cleared (especially on the success path).
            sessionManager.SetReconnecting(false);
            _reconnectCts = null;
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

    protected override async Task OnChangeProcessorStartedAsync(CancellationToken cancellationToken)
    {
        var processor = _incomingProcessor;
        if (processor is null)
        {
            return;
        }

        var session = _sessionManager?.CurrentSession;
        if (session is null || !session.Connected)
        {
            return;
        }

        var reconciled = 0;
        var visited = new HashSet<IInterceptorSubject>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        await ReconcileSubjectAsync(_subject, session, processor, visited, cancellationToken).ConfigureAwait(false);

        async Task ReconcileSubjectAsync(
            IInterceptorSubject subject,
            ISession currentSession,
            OpcUaClientIncomingEventProcessor proc,
            HashSet<IInterceptorSubject> visitedSet,
            CancellationToken ct)
        {
            if (!visitedSet.Add(subject))
            {
                return;
            }

            var registered = subject.TryGetRegisteredSubject();
            if (registered is null)
            {
                return;
            }

            foreach (var property in registered.Properties)
            {
                if (!property.CanContainSubjects)
                {
                    continue;
                }

                if (!property.Reference.TryGetPropertyData(OpcUaNodeIdKey, out _) &&
                    !property.Reference.TryGetSource(out _))
                {
                    continue;
                }

                foreach (var child in property.Children)
                {
                    if (child.Subject is null)
                    {
                        continue;
                    }

                    if (!child.Subject.TryGetData(SubjectNodeIdDataKey, out _))
                    {
                        var addedNodeId = await SendAddNodesToServerAsync(
                            child.Subject, property, child.Index, currentSession, ct).ConfigureAwait(false);
                        if (addedNodeId is not null)
                        {
                            proc.AddEcho(addedNodeId);
                            proc.SubjectMap.Add(addedNodeId, child.Subject);

                            var refDescription = new ReferenceDescription
                            {
                                NodeId = new ExpandedNodeId(addedNodeId),
                                BrowseName = new QualifiedName(
                                    addedNodeId.Identifier?.ToString() ?? "", addedNodeId.NamespaceIndex),
                                NodeClass = NodeClass.Object
                            };

                            var monitoredItems = await _subjectLoader.LoadSubjectAsync(
                                child.Subject, refDescription, currentSession, proc.SubjectMap,
                                ct).ConfigureAwait(false);

                            if (monitoredItems.Count > 0 && _sessionManager?.SubscriptionManager is { } sm)
                            {
                                await sm.AddMonitoredItemsAsync(
                                    monitoredItems, (Session)currentSession, ct).ConfigureAwait(false);
                            }

                            reconciled++;
                        }
                    }

                    await ReconcileSubjectAsync(child.Subject, currentSession, proc, visitedSet, ct).ConfigureAwait(false);
                }
            }
        }

        if (reconciled > 0)
        {
            _logger.LogInformation("Post-load reconciliation: sent AddNodes for {Count} locally-created subjects.", reconciled);
        }
    }

    public override async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (_writer is null)
        {
            return WriteResult.Failure(changes, new InvalidOperationException("OPC UA client not started."));
        }

        await ProcessOutgoingStructuralChangesAsync(changes, cancellationToken).ConfigureAwait(false);

        return await _writer.WriteChangesAsync(changes, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessOutgoingStructuralChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var processor = _incomingProcessor;
        if (processor is null)
        {
            return;
        }

        var session = _sessionManager?.CurrentSession;
        if (session is null || !session.Connected)
        {
            return;
        }

        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes.Span[i];
            var property = change.Property.TryGetRegisteredProperty();
            if (property is null || !property.CanContainSubjects)
            {
                continue;
            }

            var oldSubjects = OpcUaStructuralChangeHelper.ExtractSubjects(change.GetOldValue<object?>());
            var newSubjects = OpcUaStructuralChangeHelper.ExtractSubjects(change.GetNewValue<object?>());
            var (added, removed) = OpcUaStructuralChangeHelper.ComputeSubjectDiff(oldSubjects, newSubjects);

            foreach (var (subject, _) in removed)
            {
                NodeId? nodeId = null;
                if (subject.TryGetData(SubjectNodeIdDataKey, out var obj) && obj is NodeId id)
                {
                    nodeId = id;
                }

                if (nodeId is not null)
                {
                    await SendDeleteNodesToServerAsync(nodeId, session, cancellationToken).ConfigureAwait(false);
                    processor.AddEcho(nodeId);
                }
            }

            foreach (var (subject, index) in added)
            {
                var addedNodeId = await SendAddNodesToServerAsync(
                    subject, property, index, session, cancellationToken).ConfigureAwait(false);
                if (addedNodeId is not null)
                {
                    processor.AddEcho(addedNodeId);
                    processor.SubjectMap.Add(addedNodeId, subject);

                    var refDescription = new ReferenceDescription
                    {
                        NodeId = new ExpandedNodeId(addedNodeId),
                        BrowseName = new QualifiedName(
                            addedNodeId.Identifier?.ToString() ?? "", addedNodeId.NamespaceIndex),
                        NodeClass = NodeClass.Object
                    };

                    var monitoredItems = await _subjectLoader.LoadSubjectAsync(
                        subject, refDescription, session, processor.SubjectMap,
                        cancellationToken).ConfigureAwait(false);

                    if (monitoredItems.Count > 0 && _sessionManager?.SubscriptionManager is { } subscriptionManager)
                    {
                        await subscriptionManager.AddMonitoredItemsAsync(
                            monitoredItems, (Session)session, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private async Task<NodeId?> SendAddNodesToServerAsync(
        IInterceptorSubject subject,
        RegisteredSubjectProperty property,
        object? index,
        ISession session,
        CancellationToken cancellationToken)
    {
        var parentSubject = property.Subject;
        if (!parentSubject.TryGetData(SubjectNodeIdDataKey, out var parentNodeIdObj) ||
            parentNodeIdObj is not NodeId parentNodeId)
        {
            return null;
        }

        var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
        if (propertyName is null)
        {
            return null;
        }

        var namespaceIndex = GetNamespaceIndex(session);
        NodeId containerNodeId;
        QualifiedName browseName;

        if (property.IsSubjectCollection || property.IsSubjectDictionary)
        {
            var parentPath = parentNodeId.Identifier is string stringId ? stringId : "";
            var containerPath = string.IsNullOrEmpty(parentPath)
                ? propertyName
                : parentPath + "." + propertyName;
            containerNodeId = new NodeId(containerPath, namespaceIndex);

            browseName = property.IsSubjectCollection
                ? new QualifiedName($"{propertyName}[{index}]", namespaceIndex)
                : new QualifiedName(index?.ToString() ?? "", namespaceIndex);
        }
        else
        {
            containerNodeId = parentNodeId;
            browseName = new QualifiedName(propertyName, namespaceIndex);
        }

        try
        {
            var response = await session.AddNodesAsync(
                requestHeader: null,
                [new AddNodesItem
                {
                    ParentNodeId = new ExpandedNodeId(containerNodeId),
                    ReferenceTypeId = ReferenceTypeIds.HasComponent,
                    RequestedNewNodeId = ExpandedNodeId.Null,
                    BrowseName = browseName,
                    NodeClass = NodeClass.Object,
                    NodeAttributes = new ExtensionObject(new ObjectAttributes
                    {
                        DisplayName = new LocalizedText(browseName.Name),
                        Description = LocalizedText.Null,
                        WriteMask = 0,
                        UserWriteMask = 0,
                        EventNotifier = 0
                    }),
                    TypeDefinition = new ExpandedNodeId(ObjectTypeIds.BaseObjectType)
                }],
                cancellationToken).ConfigureAwait(false);

            if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
            {
                var nodeId = ExpandedNodeId.ToNodeId(response.Results[0].AddedNodeId, session.NamespaceUris);
                if (nodeId is not null)
                {
                    subject.SetData(SubjectNodeIdDataKey, nodeId);
                    return nodeId;
                }
            }
        }
        catch (ServiceResultException ex) when (
            ex.StatusCode == StatusCodes.BadNotSupported ||
            ex.StatusCode == StatusCodes.BadServiceUnsupported)
        {
            _logger.LogWarning("Server does not support AddNodes service.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send AddNodes for property {PropertyName}.", propertyName);
        }

        return null;
    }

    private async Task SendDeleteNodesToServerAsync(NodeId nodeId, ISession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.DeleteNodesAsync(
                requestHeader: null,
                [new DeleteNodesItem { NodeId = nodeId, DeleteTargetReferences = true }],
                cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException ex) when (
            ex.StatusCode == StatusCodes.BadNotSupported ||
            ex.StatusCode == StatusCodes.BadServiceUnsupported)
        {
            _logger.LogWarning("Server does not support DeleteNodes service.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send DeleteNodes for NodeId {NodeId}.", nodeId);
        }
    }

    private static ushort GetNamespaceIndex(ISession session)
    {
        var index = session.NamespaceUris.GetIndex("http://namotion.com/Interceptor/");
        return index >= 0 ? (ushort)index : (ushort)0;
    }

    internal void OnCurrentSessionChanged(ISession? previousSession, ISession? currentSession)
    {
        var handler = CurrentSessionChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new OpcUaCurrentSessionChangedEventArgs(previousSession, currentSession));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPC UA CurrentSessionChanged event handler threw an exception.");
        }
    }

    internal static bool IsTransientWriteError(StatusCode statusCode) => OutboundWriter.IsTransientWriteError(statusCode);

    /// <inheritdoc />
    async Task IFaultInjectable.InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken)
    {
        switch (faultType)
        {
            case FaultType.Kill:
                await KillSessionAsync().ConfigureAwait(false);
                break;
            case FaultType.Disconnect:
                await DisconnectTransportAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(faultType), faultType, null);
        }
    }

    private async Task KillSessionAsync()
    {
        var sessionManager = _sessionManager;
        if (sessionManager != null)
        {
            _logger.LogWarning("Chaos: killing OPC UA client session.");

            var reconnectCts = _reconnectCts;
            if (reconnectCts is not null)
            {
                // Manual reconnection in progress — just cancel it.
                // ReconnectSessionAsync's catch block will handle cleanup via ClearSessionAsync.
                // Calling ClearSessionAsync here would race: it disposes the session that
                // ReconnectSessionAsync just created, causing BadSessionNotActivated on the
                // next read and triggering a permanent failure loop.
                try { await reconnectCts.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { /* CTS disposed between check and cancel */ }
            }
            else
            {
                // No manual reconnection in progress — clear session directly.
                await sessionManager.ClearSessionAsync(CancellationToken.None).ConfigureAwait(false);

                // If ReconnectSessionAsync started between our _reconnectCts check and ClearSessionAsync,
                // cancel it to speed up recovery instead of waiting for it to fail naturally.
                var lateCts = _reconnectCts;
                if (lateCts is not null)
                {
                    try { await lateCts.CancelAsync().ConfigureAwait(false); }
                    catch (ObjectDisposedException) { /* CTS disposed between check and cancel */ }
                }
            }
        }
    }

    private async Task DisconnectTransportAsync(CancellationToken cancellationToken)
    {
        var sessionManager = _sessionManager;
        if (sessionManager != null)
        {
            if (sessionManager.IsReconnecting)
            {
                // Reconnection in progress — transport disconnect would destroy the session
                // being set up, causing the reconnection to fail. Skip is safe because the
                // reconnection itself will establish a fresh connection.
                _logger.LogDebug("Chaos: skipping disconnect during active reconnection.");
                return;
            }

            _logger.LogWarning("Chaos: disconnecting OPC UA client transport.");

            // Note: TOCTOU — reconnection could start between the IsReconnecting check and
            // DisconnectTransportAsync(). This is benign: the transport close triggers keep-alive
            // failure on the new session, which is caught and retried by the health check loop.
            await sessionManager.DisconnectTransportAsync(cancellationToken).ConfigureAwait(false);
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
        _isStarted = false;
        CleanupPropertyData();
    }

    private void RemoveItemsForSubject(IInterceptorSubject subject)
    {
        _structureLock.Wait();
        try
        {
            _sessionManager?.SubscriptionManager.RemoveItemsForSubject(subject);
            _sessionManager?.PollingManager?.RemoveItemsForSubject(subject);
        }
        finally
        {
            _structureLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // Already disposed
        }

        if (_modelChangeHandler is not null)
        {
            await _modelChangeHandler.DisposeAsync().ConfigureAwait(false);
            _modelChangeHandler = null;
        }

        if (_incomingProcessor is not null)
        {
            await _incomingProcessor.DisposeAsync().ConfigureAwait(false);
            if (_incomingProcessorTask is not null)
            {
                try { await _incomingProcessorTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _incomingProcessor = null;
            _incomingProcessorTask = null;
        }

        var sessionManager = _sessionManager;
        if (sessionManager is not null)
        {
            await sessionManager.DisposeAsync().ConfigureAwait(false);
            _sessionManager = null;
        }

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
