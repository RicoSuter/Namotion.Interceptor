using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Sync;

/// <summary>
/// Helper class for subscribing to OPC UA ModelChangeEvents on the client side.
/// Monitors address space structure changes and notifies the sync coordinator.
/// </summary>
internal class ModelChangeEventSubscription : IDisposable
{
    private readonly OpcUaAddressSpaceSync _sync;
    private readonly IOpcUaSyncStrategy _strategy;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposalCts = new();
    private Session? _session;
    private Subscription? _subscription;
    private MonitoredItem? _monitoredItem;
    private bool _disposed;

    public ModelChangeEventSubscription(
        OpcUaAddressSpaceSync sync,
        IOpcUaSyncStrategy strategy,
        ILogger logger)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Subscribes to ModelChangeEvents from the server.
    /// </summary>
    public void Subscribe(Session session)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ModelChangeEventSubscription));
        }

        _session = session;

        try
        {
            // Create a subscription for events
            _subscription = new Subscription(session.DefaultSubscription)
            {
                PublishingEnabled = true,
                PublishingInterval = 1000, // 1 second
                KeepAliveCount = 10,
                LifetimeCount = 100,
                MaxNotificationsPerPublish = 1000,
                Priority = 0
            };

            // Create a monitored item for the Server object's events
            _monitoredItem = new MonitoredItem(_subscription.DefaultItem)
            {
                StartNodeId = ObjectIds.Server,
                AttributeId = Opc.Ua.Attributes.EventNotifier,
                SamplingInterval = 0,
                QueueSize = 1000,
                DiscardOldest = true
            };

            // Set up event filter for GeneralModelChangeEventType
            var eventFilter = new EventFilter();
            
            // Select the fields we want from the event
            eventFilter.SelectClauses.Add(new SimpleAttributeOperand
            {
                TypeDefinitionId = ObjectTypeIds.GeneralModelChangeEventType,
                BrowsePath = new QualifiedNameCollection { Opc.Ua.BrowseNames.EventId },
                AttributeId = Opc.Ua.Attributes.Value
            });
            eventFilter.SelectClauses.Add(new SimpleAttributeOperand
            {
                TypeDefinitionId = ObjectTypeIds.GeneralModelChangeEventType,
                BrowsePath = new QualifiedNameCollection { Opc.Ua.BrowseNames.EventType },
                AttributeId = Opc.Ua.Attributes.Value
            });
            eventFilter.SelectClauses.Add(new SimpleAttributeOperand
            {
                TypeDefinitionId = ObjectTypeIds.GeneralModelChangeEventType,
                BrowsePath = new QualifiedNameCollection { Opc.Ua.BrowseNames.Time },
                AttributeId = Opc.Ua.Attributes.Value
            });
            eventFilter.SelectClauses.Add(new SimpleAttributeOperand
            {
                TypeDefinitionId = ObjectTypeIds.GeneralModelChangeEventType,
                BrowsePath = new QualifiedNameCollection { Opc.Ua.BrowseNames.Message },
                AttributeId = Opc.Ua.Attributes.Value
            });
            eventFilter.SelectClauses.Add(new SimpleAttributeOperand
            {
                TypeDefinitionId = ObjectTypeIds.GeneralModelChangeEventType,
                BrowsePath = new QualifiedNameCollection { Opc.Ua.BrowseNames.Changes },
                AttributeId = Opc.Ua.Attributes.Value
            });

            // Filter for GeneralModelChangeEventType
            eventFilter.WhereClause = new ContentFilter();
            var element = new ContentFilterElement
            {
                FilterOperator = FilterOperator.OfType,
                FilterOperands = new ExtensionObjectCollection
                {
                    new ExtensionObject(new LiteralOperand { Value = ObjectTypeIds.GeneralModelChangeEventType })
                }
            };
            eventFilter.WhereClause.Elements.Add(element);

            _monitoredItem.Filter = eventFilter;

            // Set up notification handler
            _monitoredItem.Notification += OnEventNotification;

            // Add the item to the subscription
            _subscription.AddItem(_monitoredItem);

            // Add the subscription to the session
            session.AddSubscription(_subscription);
            _subscription.Create();

            _logger.LogInformation("Subscribed to ModelChangeEvents on server");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to subscribe to ModelChangeEvents");
            Dispose();
            throw;
        }
    }

    private void OnEventNotification(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs e)
    {
        try
        {
            if (e.NotificationValue is not EventFieldList eventFields)
            {
                return;
            }

            // Parse the event fields
            if (eventFields.EventFields.Count < 5)
            {
                _logger.LogWarning("Received ModelChangeEvent with insufficient fields");
                return;
            }

            var changes = eventFields.EventFields[4].Value as ExtensionObject[];
            if (changes == null || changes.Length == 0)
            {
                return;
            }

            _logger.LogDebug("Received ModelChangeEvent with {ChangeCount} changes", changes.Length);

            // Process changes asynchronously to avoid blocking the notification handler
            if (!_disposalCts.IsCancellationRequested)
            {
                _ = ProcessChangesAsync(changes, _disposalCts.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ModelChangeEvent notification");
        }
    }

    private async Task ProcessChangesAsync(ExtensionObject[] changes, CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (change.Body is not ModelChangeStructureDataType changeData)
            {
                continue;
            }

            var verb = (ModelChangeStructureVerbMask)changeData.Verb;

            _logger.LogDebug(
                "ModelChange detected - Verb: {Verb}, Affected: {Affected}",
                verb,
                changeData.Affected);

            try
            {
                if (verb == ModelChangeStructureVerbMask.NodeAdded && changeData.Affected is NodeId affectedNodeId)
                {
                    _logger.LogInformation("ModelChangeEvent NodeAdded: {NodeId}", affectedNodeId);

                    // Browse the parent to find this node's details
                    // Note: ModelChangeEvent doesn't provide parent info directly,
                    // so we create a ReferenceDescription with available info
                    var nodeDescription = new ReferenceDescription
                    {
                        NodeId = new ExpandedNodeId(affectedNodeId),
                        BrowseName = new QualifiedName(affectedNodeId.Identifier?.ToString() ?? "Unknown"),
                        NodeClass = NodeClass.Object // Assume object, could be improved by reading node class
                    };

                    // The parent NodeId is not provided by ModelChangeEvent
                    // We pass the affected node's parent (if available from AffectedType context)
                    // For now, we use NodeId.Null and let the sync coordinator handle resolution
                    var parentNodeId = NodeId.Null;

                    await _sync.OnRemoteNodeAddedAsync(nodeDescription, parentNodeId, cancellationToken).ConfigureAwait(false);
                }
                else if (verb == ModelChangeStructureVerbMask.NodeDeleted && changeData.Affected is NodeId deletedNodeId)
                {
                    _logger.LogInformation("ModelChangeEvent NodeDeleted: {NodeId}", deletedNodeId);

                    await _sync.OnRemoteNodeRemovedAsync(deletedNodeId, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Disposal in progress - exit silently
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process ModelChangeEvent verb {Verb}", verb);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Cancel any in-flight operations first
        _disposalCts.Cancel();

        try
        {
            if (_monitoredItem != null)
            {
                _monitoredItem.Notification -= OnEventNotification;
            }

            if (_subscription != null && _session != null)
            {
                _session.RemoveSubscription(_subscription);
                _subscription.Delete(true);
                _subscription.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing ModelChangeEventSubscription");
        }

        _disposalCts.Dispose();
        _subscription = null;
        _monitoredItem = null;
        _session = null;
    }
}
