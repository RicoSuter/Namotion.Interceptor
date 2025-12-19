using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
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

    private LifecycleInterceptor? _lifecycleInterceptor;
    private SessionManager? _sessionManager;
    private SubjectPropertyWriter? _propertyWriter;

    private int _disposed; // 0 = false, 1 = true (thread-safe via Interlocked)
    private volatile bool _isStarted;
    private int _reconnectingIterations; // Tracks health check iterations while reconnecting (for stall detection)

    private IReadOnlyList<MonitoredItem>? _initialMonitoredItems;

    internal string OpcUaNodeIdKey { get; } = "OpcUaNodeId:" + Guid.NewGuid();

    public OpcUaSubjectClientSource(IInterceptorSubject subject, OpcUaClientConfiguration configuration, ILogger logger)
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

    /// <inheritdoc />
    public IInterceptorSubject RootSubject => _subject;

    /// <inheritdoc />
    public bool IsPropertyIncluded(RegisteredSubjectProperty property) =>
        _configuration.PathProvider.IsPropertyIncluded(property);

    public async Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        Reset();

        _propertyWriter = propertyWriter;
        _logger.LogInformation("Connecting to OPC UA server at {ServerUrl}.", _configuration.ServerUrl);

        _lifecycleInterceptor = _subject.Context.TryGetLifecycleInterceptor();
        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectDetached += OnSubjectDetached;
        }

        _sessionManager = new SessionManager(this, propertyWriter, _configuration, _logger);

        var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);
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

    public int WriteBatchSize => (int)(_sessionManager?.CurrentSession?.OperationLimits?.MaxNodesPerWrite ?? 0);

    public async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
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

        var propertyWriter = _propertyWriter;
        if (propertyWriter is null)
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
            propertyWriter.StartBuffering();

            // Create new session (CreateSessionAsync disposes old session internally)
            var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);
            var session = await sessionManager.CreateSessionAsync(application, _configuration, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("New OPC UA session created successfully.");

            if (_initialMonitoredItems is not null && _initialMonitoredItems.Count > 0)
            {
                await sessionManager.CreateSubscriptionsAsync(_initialMonitoredItems, session, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Subscriptions recreated successfully with {Count} monitored items.",
                    _initialMonitoredItems.Count);
            }

            await propertyWriter.CompleteInitializationAsync(cancellationToken).ConfigureAwait(false);

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
    /// Writes changes to OPC UA server atomically (all-or-nothing).
    /// Throws if any write fails, allowing the entire batch to be retried.
    /// </summary>
    public async ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var session = _sessionManager?.CurrentSession;
        if (session is null || !session.Connected)
        {
            throw new InvalidOperationException("OPC UA session is not connected.");
        }

        var writeValues = CreateWriteValuesCollection(changes);
        if (writeValues.Count is 0)
        {
            return;
        }

        var writeResponse = await session.WriteAsync(requestHeader: null, writeValues, cancellationToken).ConfigureAwait(false);
        ValidateWriteResults(writeResponse.Results);
    }

    /// <summary>
    /// Validates write results and throws if any write failed (all-or-nothing semantics).
    /// </summary>
    private void ValidateWriteResults(StatusCodeCollection results)
    {
        var transientFailureCount = 0;
        var permanentFailureCount = 0;

        for (var i = 0; i < results.Count; i++)
        {
            if (StatusCode.IsBad(results[i]))
            {
                if (IsTransientWriteError(results[i]))
                {
                    transientFailureCount++;
                }
                else
                {
                    permanentFailureCount++;
                }
            }
        }

        if (transientFailureCount > 0 || permanentFailureCount > 0)
        {
            _logger.LogWarning(
                "OPC UA write batch failed: {TransientCount} transient, {PermanentCount} permanent failures out of {TotalCount} writes.",
                transientFailureCount, permanentFailureCount, results.Count);

            if (transientFailureCount > 0)
            {
                throw new InvalidOperationException(
                    $"OPC UA write failed with {transientFailureCount} transient errors. Entire batch will be retried.");
            }

            // Only permanent errors, log but don't retry
            _logger.LogError(
                "OPC UA write batch had {PermanentCount} permanent failures. These changes will not be retried.",
                permanentFailureCount);
        }
    }

    private WriteValueCollection CreateWriteValuesCollection(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var span = changes.Span;
        var writeValues = new WriteValueCollection(span.Length);
        for (var i = 0; i < span.Length; i++)
        {
            var change = span[i];
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
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // Already disposed
        }

        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectDetached -= OnSubjectDetached;
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

    private void OnSubjectDetached(SubjectLifecycleChange change)
    {
        // Skip cleanup during reconnection (subscriptions being transferred)
        if (_sessionManager?.IsReconnecting == true)
        {
            return;
        }

        _sessionManager?.SubscriptionManager.RemoveItemsForSubject(change.Subject);
        _sessionManager?.PollingManager?.RemoveItemsForSubject(change.Subject);
        
        CleanupPropertyDataForSubject(change.Subject);
    }

    private void CleanupPropertyDataForSubject(IInterceptorSubject subject)
    {
        var toRemove = _propertiesWithOpcData
            .Where(p => p.Subject == subject)
            .ToList();

        foreach (var property in toRemove)
        {
            property.RemovePropertyData(OpcUaNodeIdKey);
            _propertiesWithOpcData.Remove(property);
        }
    }
}
