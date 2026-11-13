using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
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

    private readonly IInterceptorSubject _subject;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;

    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly SubscriptionHealthMonitor _subscriptionHealthMonitor;

    private volatile SessionManager? _sessionManager;
    private volatile SubjectPropertyWriter? _propertyWriter;
    private OutboundWriter? _writer;

    private readonly SemaphoreSlim _structureLock = new(1, 1);
    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)

    private volatile bool _isStarted;

    internal string OpcUaNodeIdKey { get; } = "OpcUaNodeId:" + Guid.NewGuid();

    internal SessionManager? SessionManager => _sessionManager;
    internal SourceOwnershipManager Ownership => _ownership;

    internal ReconnectionMetrics ReconnectionMetrics { get; } = new();

    // Owned here rather than by SessionManager, which is rebuilt on every connect attempt including
    // failed ones, so the totals they feed survive a reconnect storm.
    internal PollingMetrics PollingMetrics { get; } = new();

    internal ReadAfterWriteMetrics ReadAfterWriteMetrics { get; } = new();

    internal ThroughputCounter IncomingThroughput => Metrics.Incoming!;
    internal ThroughputCounter OutgoingThroughput => Metrics.Outgoing!;

    /// <summary>
    /// Forwards to the protected ReportConnectionLost transition seam for SessionManager, which
    /// holds this concrete source type but is not part of its inheritance hierarchy and so cannot
    /// call the protected member directly.
    /// </summary>
    internal void NotifyConnectionLost()
    {
        Metrics.MarkNotOperational();
        ReportConnectionLost();
    }

    /// <summary>
    /// Forwards a healthy-session report from <c>SessionManager</c> and the health check loop, which
    /// cannot reach the protected metrics directly. Same pattern as
    /// <see cref="NotifyConnectionLost"/>.
    /// </summary>
    internal void NotifySessionHealthy() => Metrics.MarkOperational();

    /// <summary>
    /// Drops liveness alone, for the paths that observe a session which is no longer usable but must
    /// not report a connection loss.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="NotifyConnectionLost"/> this leaves the state transition and the writer
    /// generation bump to the caller, so a caller that does neither can leave the source reading
    /// <see cref="Namotion.Interceptor.Connectors.Monitoring.SourceState.Synchronized"/> beside a
    /// client reporting itself as not operational.
    /// </remarks>
    internal void NotifySessionNotHealthy() => Metrics.MarkNotOperational();

    /// <summary>
    /// Records a failure swallowed by source-owned background work, unless that work is only failing
    /// because its lifetime is ending. Helper types use this instead of exposing the metrics writer.
    /// </summary>
    internal void ReportBackgroundError(Exception error) =>
        ReportBackgroundError(error, default);

    internal void ReportBackgroundError(Exception error, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
        {
            Metrics.ReportError(error);
        }
    }

    /// <inheritdoc />
    public override int WriteBatchSize => _writer?.WriteBatchSize ?? 0;

    /// <inheritdoc cref="SubjectSourceBase.Diagnostics" />
    public override OpcUaClientDiagnostics Diagnostics { get; }

    /// <inheritdoc />
    public ISession? CurrentSession => _sessionManager?.CurrentSession;

    /// <inheritdoc />
    public event EventHandler<OpcUaCurrentSessionChangedEventArgs>? CurrentSessionChanged;

    public OpcUaSubjectClientSource(IInterceptorSubject subject, OpcUaClientConfiguration configuration, ILogger logger)
        : base(
            subject.Context,
            logger,
            configuration.BufferTime,
            configuration.RetryTime,
            configuration.WriteRetryQueueSize,
            new ThroughputCounter(),
            new ThroughputCounter())
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

        _subjectLoader = new OpcUaSubjectLoader(subject, configuration, _ownership, this, logger);
        _subscriptionHealthMonitor = new SubscriptionHealthMonitor(logger, ReportBackgroundError);

        Metrics.RegisterClaimedProperties(() => _ownership.Count);
        Metrics.RegisterResettable(ReconnectionMetrics);
        Metrics.RegisterResettable(PollingMetrics);
        Metrics.RegisterResettable(ReadAfterWriteMetrics);

        Diagnostics = new OpcUaClientDiagnostics(this, Metrics);
    }

    private void OnSubjectDetaching(IInterceptorSubject subject)
    {
        RemoveItemsForSubject(subject);
    }

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject => _subject;

    protected override async Task<IAsyncDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        Reset();

        // Without this, a previous attempt that reached the health loop before its initial load
        // failed would keep reporting as operational for the whole retry delay.
        Metrics.MarkNotOperational();

        _propertyWriter = propertyWriter;
        _logger.LogInformation("Connecting to OPC UA server at {ServerUrl}.", _configuration.ServerUrl);

        _sessionManager = new SessionManager(
            this, propertyWriter, _configuration, PollingMetrics, ReadAfterWriteMetrics, _logger);
        _writer = new OutboundWriter(_sessionManager, _configuration, OpcUaNodeIdKey, OutgoingThroughput, _logger);

        try
        {
            var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);
            var session = await _sessionManager.CreateSessionAsync(application, _configuration, cancellationToken).ConfigureAwait(false);

            ReconnectionMetrics.RecordInitialConnection();
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

            _isStarted = true;

            var sessionManagerForLifetime = _sessionManager;
            return BackgroundTaskLifetime.Start(
                cancellationToken,
                _logger,
                RunHealthCheckLoopAsync,
                async () =>
                {
                    try { await sessionManagerForLifetime.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogWarning(ex, "OPC UA session manager threw during listen-lifetime disposal."); }

                    // Reached on every way out of this attempt, including a failure raised after this
                    // method returned, which the retry loop records without touching liveness. Must
                    // stay after the disposal that latches the manager's disposed flag: a connection
                    // state change still in flight would otherwise raise liveness again behind
                    // this call, and that stale rise would stand for the whole retry delay.
                    Metrics.MarkNotOperational();
                });
        }
        catch
        {
            // Not reported here: it rethrows into SubjectSourceBase.RunAsync, whose retry loop records
            // the same exception, so reporting it here as well would only duplicate it.
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
            var readValues = new List<ReadValueId>(take);

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
                readValues.ToArrayOf(),
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
                var value = _configuration.ValueConverter.ConvertToPropertyValue(
                    dataValue.WrappedValue.ToClrValue(), property);
                property.SetValueFromSource(this, dataValue.SourceTimestamp.ToClrTimestamp(), null, value);
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
    /// Creates MonitoredItems from owned properties for recreating the subscriptions.
    /// This recreates SDK objects on demand instead of holding them in memory.
    /// </summary>
    private IReadOnlyList<MonitoredItem> CreateMonitoredItemsForReconnection()
    {
        var ownedProperties = GetOwnedPropertiesWithNodeIds();
        var monitoredItems = new List<MonitoredItem>(ownedProperties.Count);

        foreach (var (property, nodeId) in ownedProperties)
        {
            var monitoredItem = MonitoredItemFactory.Create(_configuration, nodeId, property, _subject);
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
                if (sessionManager is not null && _propertyWriter is not null && _isStarted)
                {
                    // Reconnection is owned by the managed session; while it recovers there is nothing
                    // to heal, and liveness follows its state transitions.
                    if (sessionManager.IsConnected)
                    {
                        await HandleHealthySessionAsync(sessionManager, stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        sessionManager.ReportLivenessFromSessionState();
                    }
                }

                await Task.Delay(_configuration.SubscriptionHealthCheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                ReportBackgroundError(ex, stoppingToken);
                _logger.LogError(ex, "Error during health check. Retrying after delay.");
                try { await Task.Delay(_configuration.SubscriptionHealthCheckInterval, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        _logger.LogInformation("OPC UA client health loop has stopped.");
    }

    private async Task HandleHealthySessionAsync(SessionManager sessionManager, CancellationToken cancellationToken)
    {
        await sessionManager.PerformFullStateSyncIfNeededAsync(cancellationToken).ConfigureAwait(false);

        // Kept ahead of the network-touching steps below, so a throw in either cannot skip this
        // tick's report and leave a client that is serving subscription data reporting itself down.
        sessionManager.ReportLivenessFromSessionState();

        var subscriptionManager = sessionManager.SubscriptionManager;
        if (subscriptionManager.HasStoppedPublishing || subscriptionManager.NeedsRecreate)
        {
            _logger.LogWarning(
                "OPC UA subscriptions have stopped publishing or were not created completely. Recreating subscriptions to recover notification flow...");
            await RecreateSubscriptionsAsync(sessionManager, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _subscriptionHealthMonitor.CheckAndHealSubscriptionsAsync(
            sessionManager.Subscriptions,
            sessionManager.SubscriptionManager.RegisterRecoveredItems,
            cancellationToken).ConfigureAwait(false);

        await sessionManager.SubscriptionManager
            .EscalatePersistentlyFailedItemsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces every subscription on the current session and reloads the full state. Used when a
    /// subscription stopped publishing while the session itself stayed connected. When creating the
    /// subscriptions fails, the subscription manager reports that it needs a recreate; the full state
    /// read is requested up front, so the next health check performs it (ending the buffering) before
    /// it runs the recreate again.
    /// </summary>
    private async Task RecreateSubscriptionsAsync(SessionManager sessionManager, CancellationToken cancellationToken)
    {
        var session = sessionManager.CurrentSession;
        var propertyWriter = _propertyWriter;
        if (session is null || propertyWriter is null)
        {
            return;
        }

        sessionManager.RequestFullStateSync();
        propertyWriter.StartBuffering();

        await _structureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var staleSubscriptions = sessionManager.Subscriptions.ToList();
            if (staleSubscriptions.Count > 0)
            {
                try
                {
                    await session.RemoveSubscriptionsAsync(staleSubscriptions, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to remove stopped OPC UA subscriptions before recreating them.");
                }
            }

            var monitoredItems = CreateMonitoredItemsForReconnection();
            if (monitoredItems.Count > 0)
            {
                await sessionManager.CreateSubscriptionsAsync(monitoredItems, session, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _structureLock.Release();
        }

        await sessionManager.PerformFullStateSyncIfNeededAsync(cancellationToken, alreadyBuffering: true).ConfigureAwait(false);
    }

    private async Task<ReferenceDescription?> TryGetRootNodeAsync(ISession session, CancellationToken cancellationToken)
    {
        if (_configuration.RootPath is { Length: > 0 } rootPath)
        {
            var currentNodeId = ObjectIds.ObjectsFolder;

            for (var i = 0; i < rootPath.Length; i++)
            {
                var references = await BrowseNodeAsync(session, currentNodeId, cancellationToken).ConfigureAwait(false);
                var match = references.Find(reference => reference.BrowseName.Name == rootPath[i]);
                if (match is null)
                {
                    return null;
                }

                if (i == rootPath.Length - 1)
                {
                    return match;
                }

                currentNodeId = ExpandedNodeId.ToNodeId(match.NodeId, session.NamespaceUris);
            }
        }

        return new ReferenceDescription
        {
            NodeId = new ExpandedNodeId(ObjectIds.ObjectsFolder),
            BrowseName = new QualifiedName("Objects", 0)
        };
    }

    public override async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (_writer is null)
        {
            return WriteResult.Failure(changes, new InvalidOperationException("OPC UA client not started."));
        }

        return await _writer.WriteChangesAsync(changes, cancellationToken).ConfigureAwait(false);
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

    private Task KillSessionAsync()
    {
        var sessionManager = _sessionManager;
        if (sessionManager != null)
        {
            _logger.LogWarning("Chaos: killing OPC UA client transport.");
            sessionManager.KillTransport();
            Metrics.MarkNotOperational();
        }

        return Task.CompletedTask;
    }

    private async Task DisconnectTransportAsync(CancellationToken cancellationToken)
    {
        var sessionManager = _sessionManager;
        if (sessionManager != null)
        {
            if (sessionManager.IsReconnecting)
            {
                _logger.LogDebug("Chaos: skipping disconnect during active reconnection.");
                return;
            }

            _logger.LogWarning("Chaos: disconnecting OPC UA client transport.");
            await sessionManager.DisconnectTransportAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ArrayOf<ReferenceDescription>> BrowseNodeAsync(
        ISession session,
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

        await StopAsync(CancellationToken.None).ConfigureAwait(false);

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
