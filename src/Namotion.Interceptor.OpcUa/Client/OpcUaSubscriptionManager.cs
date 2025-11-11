using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Performance;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubscriptionManager
{
    private static readonly ObjectPool<List<OpcUaPropertyUpdate>> ChangesPool
        = new(() => new List<OpcUaPropertyUpdate>(16));

    private readonly ISubjectUpdater? _updater;
    private readonly PollingManager? _pollingManager;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<uint, RegisteredSubjectProperty> _monitoredItems = new();
    private readonly ConcurrentBag<Subscription> _subscriptions = new();

    private volatile bool _shuttingDown; // Prevents new callbacks during cleanup

    /// <summary>
    /// Gets the current list of subscriptions (thread-safe collection).
    /// </summary>
    public IReadOnlyCollection<Subscription> Subscriptions => _subscriptions;

    /// <summary>
    /// Gets the current monitored items (thread-safe dictionary).
    /// </summary>
    public IReadOnlyDictionary<uint, RegisteredSubjectProperty> MonitoredItems => _monitoredItems;

    public OpcUaSubscriptionManager(ISubjectUpdater updater, PollingManager? pollingManager, OpcUaClientConfiguration configuration, ILogger logger)
    {
        _updater = updater;
        _pollingManager = pollingManager;
        _configuration = configuration;
        _logger = logger;
    }

    public void Clear()
    {
        _monitoredItems.Clear();
        _subscriptions.Clear();
    }

    public async Task CreateBatchedSubscriptionsAsync(
        IReadOnlyList<MonitoredItem> monitoredItems,
        Session session,
        CancellationToken cancellationToken)
    {
        // Thread-safety design: Uses TEMPORAL SEPARATION to prevent race conditions
        // - Subscriptions are fully initialized (including ApplyChanges) BEFORE being added to _subscriptions
        // - Health monitor only operates on subscriptions already in _subscriptions collection
        // - No overlap possible between initialization and health monitoring
        // - No semaphore needed due to this temporal separation pattern
        //
        // This method is called only once during StartListeningAsync, never concurrently.

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
                throw new InvalidOperationException("Failed to add subscription.");
            }

            await subscription.CreateAsync(cancellationToken);
            subscription.FastDataChangeCallback += OnFastDataChange;
            
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
                // Phase 1: Apply changes to OPC UA server (subscription NOT in _subscriptions yet)
                await subscription.ApplyChangesAsync(cancellationToken);
            }
            catch (ServiceResultException sre)
            {
                _logger.LogWarning(sre, "ApplyChanges failed for a batch; attempting to keep valid OPC UA monitored items by removing failed ones.");
            }

            // Phase 2: Filter and retry failed items (subscription STILL NOT in _subscriptions)
            await FilterOutFailedMonitoredItemsAsync(subscription, cancellationToken);

            // Phase 3: Make subscription visible to health monitor (AFTER all initialization complete)
            // CRITICAL: This ordering ensures temporal separation - health monitor never sees
            // subscriptions during their initialization phase (lines 101-110)
            _subscriptions.Add(subscription);
        }
    }
    
    private void OnFastDataChange(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        // Thread-safety: This callback is invoked sequentially per subscription (OPC UA stack guarantee).
        // Multiple subscriptions can invoke callbacks concurrently, but each subscription manages
        // distinct monitored items (no overlap), so concurrent callbacks won't interfere.
        // The EnqueueOrApplyUpdate ensures changes are directly applied or enqueued in order.

        if (_shuttingDown || _updater is null)
        {
            return;
        }

        var monitoredItemsCount = notification.MonitoredItems.Count;
        if (monitoredItemsCount == 0)
        {
            return;
        }

        var receivedTimestamp = DateTimeOffset.Now;
        var changes = ChangesPool.Rent();

        for (var i = 0; i < monitoredItemsCount; i++)
        {
            var item = notification.MonitoredItems[i];
            if (_monitoredItems.TryGetValue(item.ClientHandle, out var property))
            {
                changes.Add(new OpcUaPropertyUpdate
                {
                    Property = property,
                    Value = _configuration.ValueConverter.ConvertToPropertyValue(item.Value.Value, property),
                    Timestamp = item.Value.SourceTimestamp
                });
            }
        }

        if (changes.Count > 0)
        {
            var state = (source: this, subscription, receivedTimestamp, changes);
            _updater?.EnqueueOrApplyUpdate(state, static s =>
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
                        s.source._logger.LogError(e, "Failed to apply change for property {PropertyName}.", change.Property.Name);
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
        // Clear old subscriptions and add transferred ones
        _subscriptions.Clear();

        foreach (var subscription in transferredSubscriptions)
        {
            subscription.FastDataChangeCallback -= OnFastDataChange;
            subscription.FastDataChangeCallback += OnFastDataChange;
            _subscriptions.Add(subscription);
        }

        _logger.LogInformation("Updated subscription manager with {Count} transferred subscriptions", transferredSubscriptions.Count);
    }
    
    private async Task FilterOutFailedMonitoredItemsAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        List<MonitoredItem>? itemsToRemove = null;
        List<MonitoredItem>? itemsToPolled = null;

        foreach (var monitoredItem in subscription.MonitoredItems)
        {
            if (SubscriptionHealthMonitor.IsUnhealthy(monitoredItem))
            {
                itemsToRemove ??= [];
                itemsToRemove.Add(monitoredItem);

                _monitoredItems.TryRemove(monitoredItem.ClientHandle, out _);

                var statusCode = monitoredItem.Status?.Error?.StatusCode ?? StatusCodes.Good;

                // Check if we should fall back to polling for this item
                if (_configuration.EnablePollingFallback &&
                    _pollingManager != null &&
                    IsSubscriptionUnsupported(statusCode))
                {
                    itemsToPolled ??= [];
                    itemsToPolled.Add(monitoredItem);
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

        if (itemsToRemove?.Count > 0)
        {
            // Remove failed items from subscription
            foreach (var monitoredItem in itemsToRemove)
            {
                subscription.RemoveItem(monitoredItem);
            }

            try
            {
                await subscription.ApplyChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ApplyChanges after removing failed items still failed. Continuing with remaining OPC UA monitored items.");
            }

            // Add items that support polling to polling manager
            if (itemsToPolled?.Count > 0 && _pollingManager != null)
            {
                foreach (var item in itemsToPolled)
                {
                    _pollingManager.AddItem(item);
                }
            }

            var removed = itemsToRemove.Count;
            var polled = itemsToPolled?.Count;

            if (polled > 0)
            {
                _logger.LogWarning(
                    "Removed {Removed} failed monitored items from subscription " +
                    "{SubscriptionId}. {Polled} items switched to polling fallback.",
                    removed, subscription.Id, polled);
            }
            else
            {
                _logger.LogWarning(
                    "Removed {Removed} failed monitored items " +
                    "from subscription {SubscriptionId}.",
                    removed, subscription.Id);
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

    public void Dispose()
    {
        _shuttingDown = true;

        // Take snapshot and clear
        var subscriptions = _subscriptions.ToArray();
        _subscriptions.Clear();

        foreach (var subscription in subscriptions)
        {
            subscription.FastDataChangeCallback -= OnFastDataChange;
            subscription.Delete(true);
        }
    }
}
