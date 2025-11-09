using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
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

    private readonly ILogger _logger;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ConcurrentDictionary<uint, RegisteredSubjectProperty> _monitoredItems = new();
    private readonly OpcUaSubscriptionHealthMonitor _healthMonitor;
    private OpcUaPollingManager? _pollingManager;

    private ImmutableArray<Subscription> _subscriptions = ImmutableArray<Subscription>.Empty;
    private ISubjectUpdater? _updater;

    /// <summary>
    /// Gets the current list of subscriptions. Lock-free and allocation-free when accessed.
    /// Returns the immutable array directly - no copying or locking required.
    /// </summary>
    public IReadOnlyList<Subscription> Subscriptions => _subscriptions;

    public int TotalMonitoredItemCount => _monitoredItems.Count;

    public OpcUaSubscriptionManager(OpcUaClientConfiguration configuration, ILogger logger)
    {
        _configuration = configuration;
        _logger = logger;

        _healthMonitor = new OpcUaSubscriptionHealthMonitor(configuration, this, logger);
    }

    public void SetUpdater(ISubjectUpdater updater)
    {
        _updater = updater;
    }

    public void SetPollingManager(OpcUaPollingManager pollingManager)
    {
        _pollingManager = pollingManager;
    }

    public void Clear()
    {
        _monitoredItems.Clear();
        _subscriptions = ImmutableArray<Subscription>.Empty;
        Interlocked.MemoryBarrier(); // Ensure write is visible to all threads
    }

    public async Task CreateBatchedSubscriptionsAsync(
        IReadOnlyList<MonitoredItem> monitoredItems,
        Session session,
        CancellationToken cancellationToken)
    {
        var itemCount = monitoredItems.Count;
        var maximumItemsPerSubscription = _configuration.MaximumItemsPerSubscription;

        // Calculate expected subscription count for builder capacity
        var expectedSubscriptionCount = (itemCount + maximumItemsPerSubscription - 1) / maximumItemsPerSubscription;
        var builder = ImmutableArray.CreateBuilder<Subscription>(expectedSubscriptionCount);

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

            builder.Add(subscription);

            var batchEnd = Math.Min(i + maximumItemsPerSubscription, itemCount);
            for (var j = i; j < batchEnd; j++)
            {
                var item = monitoredItems[j];
                subscription.AddItem(item);

                if (item.Handle is RegisteredSubjectProperty p)
                {
                    _monitoredItems[item.ClientHandle] = p;
                }
            }

            try
            {
                await subscription.ApplyChangesAsync(cancellationToken);
            }
            catch (ServiceResultException sre)
            {
                _logger.LogWarning(sre, "ApplyChanges failed for a batch; attempting to keep valid OPC UA monitored items by removing failed ones.");
            }

            var (removed, polled) = await FilterOutFailedMonitoredItemsAsync(subscription, cancellationToken);
            if (removed > 0)
            {
                if (polled > 0)
                {
                    _logger.LogWarning("Removed {Removed} failed monitored items from subscription {SubscriptionId}. {Polled} items switched to polling fallback.",
                        removed, subscription.Id, polled);
                }
                else
                {
                    _logger.LogWarning("Removed {Removed} failed monitored items from subscription {SubscriptionId}.",
                        removed, subscription.Id);
                }
            }

            _logger.LogInformation("Created OPC UA subscription {SubscriptionId} with {Count} monitored items.", subscription.Id, subscription.MonitoredItems.Count());
        }

        // Replace subscriptions array with memory barrier to ensure visibility across threads
        // ImmutableArray is a struct, so we use memory barrier instead of Volatile.Write
        var newSubscriptions = builder.ToImmutable();
        _subscriptions = newSubscriptions;
        Interlocked.MemoryBarrier(); // Ensure write is visible to all threads
    }

    public void StartHealthMonitoring()
    {
        _healthMonitor.Start();
    }

    /// <summary>
    /// Updates the subscription list to reference subscriptions transferred by SessionReconnectHandler.
    /// Called after successful session transfer to embrace OPC Foundation's subscription preservation.
    /// </summary>
    public void UpdateTransferredSubscriptions(IEnumerable<Subscription> transferredSubscriptions)
    {
        var subscriptionList = transferredSubscriptions.ToList();
        if (subscriptionList.Count == 0)
        {
            _logger.LogWarning("UpdateTransferredSubscriptions called with empty collection");
            return;
        }

        var builder = ImmutableArray.CreateBuilder<Subscription>(subscriptionList.Count);
        foreach (var subscription in subscriptionList)
        {
            builder.Add(subscription);
            
            subscription.FastDataChangeCallback -= OnFastDataChange;
            subscription.FastDataChangeCallback += OnFastDataChange;
        }

        // Update subscription reference with memory barrier to ensure visibility across threads
        // ImmutableArray is a struct, so we use memory barrier instead of Volatile.Write
        var newSubscriptions = builder.ToImmutable();
        _subscriptions = newSubscriptions;
        Interlocked.MemoryBarrier(); // Ensure write is visible to all threads

        _logger.LogInformation("Updated subscription manager with {Count} transferred subscriptions", subscriptionList.Count);
    }
    
    private void OnFastDataChange(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        // Thread-safety: This callback is invoked sequentially per subscription (OPC UA stack guarantee).
        // Multiple subscriptions can invoke callbacks concurrently, but each subscription manages
        // distinct monitored items (no overlap), so concurrent callbacks won't interfere.
        // The EnqueueOrApplyUpdate ensures changes are applied/enqueued in order.
        
        if (_updater is null)
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
                        s.source._logger.LogError(e, "Failed to apply change for OPC UA {Path}.", change.Property.Name);
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
    
    private async Task<(int removed, int polled)> FilterOutFailedMonitoredItemsAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        List<MonitoredItem>? itemsToRemove = null;
        List<MonitoredItem>? itemsToPolled = null;

        foreach (var monitoredItem in subscription.MonitoredItems)
        {
            if (OpcUaSubscriptionHealthMonitor.IsUnhealthy(monitoredItem))
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

            return (itemsToRemove.Count, itemsToPolled?.Count ?? 0);
        }

        return (0, 0);
    }

    /// <summary>
    /// Checks if a status code indicates that subscriptions are not supported for this node.
    /// These items should fall back to polling if enabled.
    /// </summary>
    private static bool IsSubscriptionUnsupported(StatusCode statusCode)
    {
        // BadNotSupported - Server doesn't support subscriptions for this node
        // BadMonitoredItemFilterUnsupported - Filter not supported (data change filter)
        // BadAttributeIdInvalid - Attribute doesn't exist or can't be subscribed to
        return statusCode == StatusCodes.BadNotSupported ||
               statusCode == StatusCodes.BadMonitoredItemFilterUnsupported ||
               statusCode == StatusCodes.BadAttributeIdInvalid;
    }

    public void Cleanup()
    {
        _healthMonitor.Stop();

        // Capture current subscriptions and clear with memory barrier (lock-free)
        var subscriptions = _subscriptions;
        _subscriptions = ImmutableArray<Subscription>.Empty;
        Interlocked.MemoryBarrier(); // Ensure write is visible to all threads

        // Clean up captured subscriptions - unsubscribe before delete to prevent callbacks on disposed objects
        foreach (var subscription in subscriptions)
        {
            try
            {
                subscription.FastDataChangeCallback -= OnFastDataChange;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to unsubscribe FastDataChangeCallback for subscription {Id}", subscription.Id);
            }

            try
            {
                subscription.Delete(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete subscription {Id}", subscription.Id);
            }
        }
    }

    public void Dispose()
    {
        _healthMonitor.Dispose();
        Cleanup();
    }
}
