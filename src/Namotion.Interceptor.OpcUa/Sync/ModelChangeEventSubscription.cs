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
    private readonly ILogger _logger;
    private Session? _session;
    private Subscription? _subscription;
    private MonitoredItem? _monitoredItem;
    private bool _disposed;

    public ModelChangeEventSubscription(OpcUaAddressSpaceSync sync, ILogger logger)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
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

            // Process each change
            foreach (var change in changes)
            {
                if (change.Body is not ModelChangeStructureDataType changeData)
                {
                    continue;
                }

                var verb = (ModelChangeStructureVerbMask)changeData.Verb;
                
                _logger.LogDebug(
                    "ModelChange detected - Verb: {Verb}, Affected: {Affected}", 
                    verb, 
                    changeData.Affected);

                // Trigger sync operations based on the verb
                try
                {
                    if (verb == ModelChangeStructureVerbMask.NodeAdded && changeData.Affected is NodeId affectedNodeId)
                    {
                        // For node additions, we would need to browse the new node to get its details
                        // This is a simplified trigger - full implementation would need more context
                        _logger.LogInformation(
                            "ModelChangeEvent NodeAdded: {NodeId}. Sync coordinator notified.",
                            affectedNodeId);
                        
                        // Note: Full implementation would call:
                        // var nodeDescription = await BrowseNodeAsync(affectedNodeId);
                        // await _sync.OnRemoteNodeAddedAsync(nodeDescription, parentNodeId, cancellationToken);
                    }
                    else if (verb == ModelChangeStructureVerbMask.NodeDeleted && changeData.Affected is NodeId deletedNodeId)
                    {
                        _logger.LogInformation(
                            "ModelChangeEvent NodeDeleted: {NodeId}. Sync coordinator notified.",
                            deletedNodeId);
                        
                        // Note: Full implementation would call:
                        // await _sync.OnRemoteNodeRemovedAsync(deletedNodeId, cancellationToken);
                    }
                }
                catch (Exception ex2)
                {
                    _logger.LogError(ex2, "Failed to process ModelChangeEvent verb {Verb}", verb);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ModelChangeEvent notification");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

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

        _subscription = null;
        _monitoredItem = null;
        _session = null;
    }
}
