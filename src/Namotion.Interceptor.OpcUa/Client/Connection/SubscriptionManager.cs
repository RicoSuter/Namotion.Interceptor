using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Performance;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

internal class SubscriptionManager
{
    private static readonly ObjectPool<List<PropertyUpdate>> ChangesPool
        = new(() => new List<PropertyUpdate>(16));

    private readonly OpcUaSubjectClientSource _source;
    private readonly SubjectPropertyWriter? _propertyWriter;
    private readonly PollingManager? _pollingManager;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<uint, RegisteredSubjectProperty> _monitoredItems = new();
    private readonly ConcurrentDictionary<Subscription, byte> _subscriptions = new();

    private volatile bool _shuttingDown; // Prevents new callbacks during cleanup

    /// <summary>
    /// Gets the current list of subscriptions (thread-safe collection).
    /// </summary>
    public IReadOnlyCollection<Subscription> Subscriptions => (IReadOnlyCollection<Subscription>)_subscriptions.Keys;

    /// <summary>
    /// Gets the current monitored items (thread-safe dictionary).
    /// </summary>
    public IReadOnlyDictionary<uint, RegisteredSubjectProperty> MonitoredItems => _monitoredItems;

    public SubscriptionManager(OpcUaSubjectClientSource source, SubjectPropertyWriter propertyWriter, PollingManager? pollingManager, OpcUaClientConfiguration configuration, ILogger logger)
    {
        _source = source;
        _propertyWriter = propertyWriter;
        _pollingManager = pollingManager;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task CreateBatchedSubscriptionsAsync(
        IReadOnlyList<MonitoredItem> monitoredItems,
        Session session,
        CancellationToken cancellationToken)
    {
        // Temporal separation: subscriptions added to _subscriptions AFTER initialization prevents health monitor races.
        _shuttingDown = false;

        var itemCount = monitoredItems.Count;
        var maximumItemsPerSubscription = _configuration.MaximumItemsPerSubscription;
        for (var i = 0; i < itemCount; i += maximumItemsPerSubscription)
        {
            var subscription = new Subscription(session.DefaultSubscription)
            {
                PublishingEnabled = true,
                PublishingInterval = _configuration.DefaultPublishingInterval,
                DisableMonitoredItemCache = true, // not needed as we use fast data change callback
                MinLifetimeInterval = 60_000,
                KeepAliveCount = _configuration.SubscriptionKeepAliveCount,
                LifetimeCount = _configuration.SubscriptionLifetimeCount,
                Priority = _configuration.SubscriptionPriority,
                MaxNotificationsPerPublish = _configuration.SubscriptionMaximumNotificationsPerPublish,
            };

            if (!session.AddSubscription(subscription))
            {
                throw new InvalidOperationException("Failed to add OPC UA subscription.");
            }

            subscription.FastDataChangeCallback += OnFastDataChange;
            await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);

            var batchEnd = Math.Min(i + maximumItemsPerSubscription, itemCount);
            for (var j = i; j < batchEnd; j++)
            {
                var item = monitoredItems[j];
                subscription.AddItem(item);

                if (item.Handle is RegisteredSubjectProperty property)
                {
                    _monitoredItems[item.ClientHandle] = property;
                }
            }

            try
            {
                await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceResultException sre)
            {
                _logger.LogWarning(sre, "ApplyChanges failed for a batch; attempting to keep valid OPC UA monitored items by removing failed ones.");
            }

            await FilterOutFailedMonitoredItemsAsync(subscription, cancellationToken).ConfigureAwait(false);

            // Add to collection AFTER initialization (temporal separation - health monitor never sees partial state)
            _subscriptions.TryAdd(subscription, 0);
        }
    }
    
    private void OnFastDataChange(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        var propertyWriter = _propertyWriter;
        if (_shuttingDown || propertyWriter is null)
        {
            return;
        }

        var monitoredItemsCount = notification.MonitoredItems.Count;
        if (monitoredItemsCount == 0)
        {
            return;
        }

        var receivedTimestamp = DateTimeOffset.UtcNow;
        var changes = ChangesPool.Rent();

        for (var i = 0; i < monitoredItemsCount; i++)
        {
            var item = notification.MonitoredItems[i];
            if (_monitoredItems.TryGetValue(item.ClientHandle, out var property))
            {
                changes.Add(new PropertyUpdate
                {
                    Property = property,
                    Value = _configuration.ValueConverter.ConvertToPropertyValue(item.Value.Value, property),
                    Timestamp = item.Value.SourceTimestamp
                });
            }
        }

        if (changes.Count > 0)
        {
            // Pool item returned inside callback. Safe because ApplyUpdate never throws:
            // It wraps callback execution in try-catch and only throws on catastrophic failures (lock/memory corruption).
            var state = (source: _source, subscription, receivedTimestamp, changes, logger: _logger);
            propertyWriter.Write(state, static s =>
            {
                for (var i = 0; i < s.changes.Count; i++)
                {
                    var change = s.changes[i];
                    try
                    {
                        change.Property.SetValueFromSource(s.source, change.Timestamp, s.receivedTimestamp, change.Value);
                    }
                    catch (Exception e)
                    {
                        s.logger.LogError(e, "Failed to apply change for property {PropertyName}.", change.Property.Name);
                    }
                }

                s.changes.Clear();
                ChangesPool.Return(s.changes);
            });
        }
        else
        {
            ChangesPool.Return(changes);
        }
    }

    /// <summary>
    /// Updates the subscription list to reference subscriptions transferred by SessionReconnectHandler.
    /// Called after successful session transfer to embrace OPC Foundation's subscription preservation.
    /// </summary>
    public void UpdateTransferredSubscriptions(IReadOnlyCollection<Subscription> transferredSubscriptions)
    {
        var oldSubscriptions = _subscriptions.Keys.ToArray();
        foreach (var subscription in transferredSubscriptions)
        {
            subscription.FastDataChangeCallback -= OnFastDataChange;
            subscription.FastDataChangeCallback += OnFastDataChange;
            _subscriptions.TryAdd(subscription, 0);
        }

        foreach (var oldSubscription in oldSubscriptions)
        {
            _subscriptions.TryRemove(oldSubscription, out _);
            oldSubscription.FastDataChangeCallback -= OnFastDataChange;
        }

        _logger.LogInformation("Updated subscription manager with {Count} transferred subscriptions (removed {OldCount} old)",
            transferredSubscriptions.Count, oldSubscriptions.Length);
    }
    
    private async Task FilterOutFailedMonitoredItemsAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        List<MonitoredItem>? failedItems = null;
        List<MonitoredItem>? polledItems = null;

        foreach (var monitoredItem in subscription.MonitoredItems)
        {
            if (SubscriptionHealthMonitor.IsUnhealthy(monitoredItem))
            {
                failedItems ??= [];
                failedItems.Add(monitoredItem);

                _monitoredItems.TryRemove(monitoredItem.ClientHandle, out _);

                var statusCode = monitoredItem.Status?.Error?.StatusCode ?? StatusCodes.Good;

                // Check if we should fall back to polling for this item
                if (_configuration.EnablePollingFallback &&
                    _pollingManager != null &&
                    IsSubscriptionUnsupported(statusCode))
                {
                    polledItems ??= [];
                    polledItems.Add(monitoredItem);
                    _logger.LogWarning("Monitored item {DisplayName} does not support subscriptions ({Status}), falling back to polling",
                        monitoredItem.DisplayName, statusCode);
                }
                else
                {
                    _logger.LogError("OPC UA monitored item creation failed for {DisplayName} (Handle={Handle}): {Status}",
                        monitoredItem.DisplayName, monitoredItem.ClientHandle, statusCode);
                }
            }
        }

        if (failedItems?.Count > 0)
        {
            foreach (var monitoredItem in failedItems)
            {
                subscription.RemoveItem(monitoredItem);
            }

            try
            {
                await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ApplyChanges after removing failed items still failed. Continuing with remaining OPC UA monitored items.");
            }

            // Add items that support polling to polling manager
            if (polledItems?.Count > 0 && _pollingManager != null)
            {
                foreach (var item in polledItems)
                {
                    _pollingManager.AddItem(item);
                }
            }

            var failedCount = failedItems.Count;
            var polledCount = polledItems?.Count;
            if (polledCount > 0)
            {
                _logger.LogWarning(
                    "Removed {Removed} failed monitored items from subscription " +
                    "{SubscriptionId}. {Polled} items switched to polling fallback.",
                    failedCount, subscription.Id, polledCount);
            }
            else
            {
                _logger.LogWarning(
                    "Removed {Removed} failed monitored items " +
                    "from subscription {SubscriptionId}.",
                    failedCount, subscription.Id);
            }
        }
    }

    /// <summary>
    /// Checks if a status code indicates that subscriptions are not supported for this node.
    /// These items should fall back to polling if enabled.
    /// </summary>
    private static bool IsSubscriptionUnsupported(StatusCode statusCode)
    {
        // BadNotSupported - Server doesn't support subscriptions for this node
        // BadMonitoredItemFilterUnsupported - Filter not supported (data change filter)
        // Note: BadAttributeIdInvalid is a permanent error - polling won't work either, so excluded
        return statusCode == StatusCodes.BadNotSupported ||
               statusCode == StatusCodes.BadMonitoredItemFilterUnsupported;
    }

    /// <summary>
    /// Removes monitored items for a detached subject. Idempotent.
    /// Note: OPC UA subscription items remain on server until session ends.
    /// This just cleans up local tracking to avoid memory leaks.
    /// </summary>
    public void RemoveItemsForSubject(IInterceptorSubject subject)
    {
        foreach (var kvp in _monitoredItems)
        {
            if (kvp.Value.Reference.Subject == subject)
            {
                _monitoredItems.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Adds monitored items for a dynamically attached subject.
    /// Uses the first available subscription with capacity, or creates a new one.
    /// </summary>
    public async Task AddMonitoredItemsForSubjectAsync(
        RegisteredSubject registeredSubject,
        Session session,
        CancellationToken cancellationToken)
    {
        var monitoredItems = new List<MonitoredItem>();

        foreach (var property in registeredSubject.Properties)
        {
            if (property.Reference.TryGetPropertyData(_source.OpcUaNodeIdKey, out var nodeIdData) && nodeIdData is NodeId nodeId)
            {
                var opcUaNodeAttribute = property.TryGetOpcUaNodeAttribute();
                var monitoredItem = new MonitoredItem
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
        }

        if (monitoredItems.Count == 0)
        {
            return;
        }

        await AddMonitoredItemsBatchedAsync(monitoredItems, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds monitored items to existing subscriptions with capacity, or creates new ones as needed.
    /// </summary>
    private async Task AddMonitoredItemsBatchedAsync(
        List<MonitoredItem> monitoredItems,
        Session session,
        CancellationToken cancellationToken)
    {
        var remainingItems = monitoredItems.ToList();
        var maximumItemsPerSubscription = _configuration.MaximumItemsPerSubscription;

        // First, try to add to existing subscriptions with capacity
        foreach (var subscription in _subscriptions.Keys)
        {
            if (remainingItems.Count == 0)
            {
                break;
            }

            var availableCapacity = maximumItemsPerSubscription - (int)subscription.MonitoredItemCount;
            if (availableCapacity <= 0)
            {
                continue;
            }

            var itemsToAdd = remainingItems.Take(availableCapacity).ToList();
            foreach (var item in itemsToAdd)
            {
                subscription.AddItem(item);

                if (item.Handle is RegisteredSubjectProperty property)
                {
                    _monitoredItems[item.ClientHandle] = property;
                }

                remainingItems.Remove(item);
            }

            try
            {
                await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply changes when adding dynamic monitored items");
            }
        }

        // Create new subscriptions for remaining items if needed
        if (remainingItems.Count > 0)
        {
            var subscription = new Subscription(session.DefaultSubscription)
            {
                PublishingEnabled = true,
                PublishingInterval = _configuration.DefaultPublishingInterval,
                DisableMonitoredItemCache = true,
                MinLifetimeInterval = 60_000,
                KeepAliveCount = _configuration.SubscriptionKeepAliveCount,
                LifetimeCount = _configuration.SubscriptionLifetimeCount,
                Priority = _configuration.SubscriptionPriority,
                MaxNotificationsPerPublish = _configuration.SubscriptionMaximumNotificationsPerPublish,
            };

            if (!session.AddSubscription(subscription))
            {
                _logger.LogWarning("Failed to add subscription for dynamic subject");
                return;
            }

            subscription.FastDataChangeCallback += OnFastDataChange;
            await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);

            foreach (var item in remainingItems)
            {
                subscription.AddItem(item);

                if (item.Handle is RegisteredSubjectProperty property)
                {
                    _monitoredItems[item.ClientHandle] = property;
                }
            }

            try
            {
                await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply changes for new dynamic subscription");
            }

            _subscriptions.TryAdd(subscription, 0);
        }
    }

    public void Dispose()
    {
        _shuttingDown = true;

        var subscriptions = _subscriptions.Keys.ToArray();
        _subscriptions.Clear();

        foreach (var subscription in subscriptions)
        {
            subscription.FastDataChangeCallback -= OnFastDataChange;
            subscription.Delete(true);
        }
    }
}
