using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Subscribes to OPC UA <see cref="ObjectTypeIds.GeneralModelChangeEventType"/> on the server
/// and forwards NodeAdded/NodeDeleted events to a callback.
/// </summary>
internal sealed class OpcUaModelChangeEventHandler : IAsyncDisposable
{
    private readonly Func<ModelChangeStructureVerbMask, NodeId, CancellationToken, Task> _onChangeDetected;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposalCts = new();

    private Session? _session;
    private Subscription? _subscription;
    private MonitoredItem? _monitoredItem;
    private bool _disposed;

    public OpcUaModelChangeEventHandler(
        Func<ModelChangeStructureVerbMask, NodeId, CancellationToken, Task> onChangeDetected,
        ILogger logger)
    {
        _onChangeDetected = onChangeDetected ?? throw new ArgumentNullException(nameof(onChangeDetected));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Creates a subscription on the session for GeneralModelChangeEvents.
    /// </summary>
    public async Task SubscribeAsync(Session session, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session);

        _session = session;

        try
        {
            _subscription = new Subscription(session.DefaultSubscription)
            {
                PublishingEnabled = true,
                PublishingInterval = 1000,
                KeepAliveCount = 10,
                LifetimeCount = 100,
                MaxNotificationsPerPublish = 1000,
                Priority = 0,
            };

            _monitoredItem = new MonitoredItem(_subscription.DefaultItem)
            {
                StartNodeId = ObjectIds.Server,
                AttributeId = Opc.Ua.Attributes.EventNotifier,
                SamplingInterval = 0,
                QueueSize = 1000,
                DiscardOldest = true,
            };

            var eventFilter = new EventFilter();

            eventFilter.SelectClauses.Add(CreateSelectClause(BrowseNames.EventId));
            eventFilter.SelectClauses.Add(CreateSelectClause(BrowseNames.EventType));
            eventFilter.SelectClauses.Add(CreateSelectClause(BrowseNames.Time));
            eventFilter.SelectClauses.Add(CreateSelectClause(BrowseNames.Message));
            eventFilter.SelectClauses.Add(CreateSelectClause(BrowseNames.Changes));

            eventFilter.WhereClause = new ContentFilter();
            eventFilter.WhereClause.Elements.Add(new ContentFilterElement
            {
                FilterOperator = FilterOperator.OfType,
                FilterOperands = new ExtensionObjectCollection
                {
                    new ExtensionObject(new LiteralOperand
                    {
                        Value = ObjectTypeIds.GeneralModelChangeEventType,
                    }),
                },
            });

            _monitoredItem.Filter = eventFilter;
            _monitoredItem.Notification += OnEventNotification;

            _subscription.AddItem(_monitoredItem);
            session.AddSubscription(_subscription);
            await _subscription.CreateAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Subscribed to GeneralModelChangeEvents on server.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to subscribe to GeneralModelChangeEvents.");
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposalCts.Cancel();

        try
        {
            if (_monitoredItem is not null)
            {
                _monitoredItem.Notification -= OnEventNotification;
            }

            if (_subscription is not null && _session is not null)
            {
                await _session.RemoveSubscriptionsAsync(
                    new[] { _subscription },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error disposing OpcUaModelChangeEventHandler.");
        }

        _disposalCts.Dispose();
        _subscription = null;
        _monitoredItem = null;
        _session = null;
    }

    private static SimpleAttributeOperand CreateSelectClause(string browseName)
    {
        return new SimpleAttributeOperand
        {
            TypeDefinitionId = ObjectTypeIds.GeneralModelChangeEventType,
            BrowsePath = new QualifiedNameCollection { new QualifiedName(browseName) },
            AttributeId = Opc.Ua.Attributes.Value,
        };
    }

    private void OnEventNotification(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs eventArgs)
    {
        try
        {
            if (eventArgs.NotificationValue is not EventFieldList eventFields)
            {
                return;
            }

            // We expect 5 fields: EventId, EventType, Time, Message, Changes.
            if (eventFields.EventFields.Count < 5)
            {
                _logger.LogWarning(
                    "Received GeneralModelChangeEvent with insufficient fields (expected 5, got {FieldCount}).",
                    eventFields.EventFields.Count);
                return;
            }

            var changes = eventFields.EventFields[4].Value as ExtensionObject[];
            if (changes is null || changes.Length == 0)
            {
                return;
            }

            _logger.LogDebug("Received GeneralModelChangeEvent with {ChangeCount} change(s).", changes.Length);

            if (!_disposalCts.IsCancellationRequested)
            {
                _ = ProcessChangesAsync(changes, _disposalCts.Token);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error handling GeneralModelChangeEvent notification.");
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

            if (verb is not (ModelChangeStructureVerbMask.NodeAdded or ModelChangeStructureVerbMask.NodeDeleted))
            {
                continue;
            }

            if (changeData.Affected is not NodeId affectedNodeId)
            {
                continue;
            }

            _logger.LogDebug(
                "ModelChange: verb={Verb}, affectedNodeId={AffectedNodeId}.",
                verb,
                affectedNodeId);

            try
            {
                await _onChangeDetected(verb, affectedNodeId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Error processing ModelChange verb={Verb} for node {AffectedNodeId}.",
                    verb,
                    affectedNodeId);
            }
        }
    }
}
