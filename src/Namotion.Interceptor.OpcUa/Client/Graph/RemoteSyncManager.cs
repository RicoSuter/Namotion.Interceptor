using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.Graph;

/// <summary>
/// Manages remote sync: ModelChangeEvents subscription and periodic resync.
/// Extracted from OpcUaSubjectClientSource.
/// </summary>
internal class RemoteSyncManager : IDisposable
{
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;

    // Periodic resync timer for servers that don't support ModelChangeEvents
    private Timer? _periodicResyncTimer;
    private volatile bool _periodicResyncInProgress;

    // ModelChangeEvent subscription tracking
    private MonitoredItem? _modelChangeEventItem;

    // Callbacks for event handling
    private OpcUaGraphChangeProcessor? _nodeChangeProcessor;
    private Func<ISession?>? _getCurrentSession;
    private Func<bool>? _isStarted;
    private Func<bool>? _isDisposed;
    private SubscriptionManager? _subscriptionManager;

    public RemoteSyncManager(
        OpcUaClientConfiguration configuration,
        ILogger logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Initializes the sync manager with the required callbacks and processors.
    /// </summary>
    public void Initialize(
        OpcUaGraphChangeProcessor nodeChangeProcessor,
        SubscriptionManager subscriptionManager,
        Func<ISession?> getCurrentSession,
        Func<bool> isStarted,
        Func<bool> isDisposed)
    {
        _nodeChangeProcessor = nodeChangeProcessor;
        _subscriptionManager = subscriptionManager;
        _getCurrentSession = getCurrentSession;
        _isStarted = isStarted;
        _isDisposed = isDisposed;
    }

    /// <summary>
    /// Sets up ModelChangeEvent subscription if enabled.
    /// </summary>
    public async Task SetupModelChangeEventSubscriptionAsync(Session session, CancellationToken cancellationToken)
    {
        if (!_configuration.EnableModelChangeEvents)
        {
            return;
        }

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
            var subscriptionManager = _subscriptionManager;
            if (subscriptionManager is not null)
            {
                // Subscribe to event notifications via FastEventCallback (since DisableMonitoredItemCache=true)
                subscriptionManager.EventNotificationReceived += OnEventNotificationReceived;

                await subscriptionManager.AddMonitoredItemsAsync([_modelChangeEventItem], session, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Starts the periodic resync timer if enabled.
    /// </summary>
    public void StartPeriodicResyncTimer()
    {
        if (!_configuration.EnablePeriodicResync)
        {
            return;
        }

        var interval = _configuration.PeriodicResyncInterval;
        _periodicResyncTimer = new Timer(OnPeriodicResyncTimerCallback, null, interval, interval);
    }

    /// <summary>
    /// Stops and cleans up all sync resources.
    /// </summary>
    public void Stop()
    {
        // Stop and dispose periodic resync timer
        _periodicResyncTimer?.Dispose();
        _periodicResyncTimer = null;
        _periodicResyncInProgress = false;

        // Clean up ModelChangeEvent subscription
        var subscriptionManager = _subscriptionManager;
        if (subscriptionManager is not null)
        {
            subscriptionManager.EventNotificationReceived -= OnEventNotificationReceived;
        }
        _modelChangeEventItem = null;
    }

    /// <summary>
    /// Resets the sync manager for a fresh connection.
    /// </summary>
    public void Reset()
    {
        Stop();
    }

    public void Dispose()
    {
        Stop();
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
        var nodeChangeProcessor = _nodeChangeProcessor;
        if (nodeChangeProcessor is null)
        {
            return;
        }

        var session = _getCurrentSession?.Invoke();
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
                        await nodeChangeProcessor.ProcessModelChangeEventAsync(modelChanges, session, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing ModelChangeEvent.");
                    }
                });
            }
        }
    }

    private void OnPeriodicResyncTimerCallback(object? state)
    {
        if (_isDisposed?.Invoke() == true ||
            _periodicResyncInProgress ||
            _isStarted?.Invoke() != true)
        {
            return;
        }

        var session = _getCurrentSession?.Invoke();
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

        _logger.LogDebug("Starting periodic resync.");
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
}
