using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Client.Polling;
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

    private readonly Lock _subscriptionsLock = new(); // Protects ImmutableArray assignment (struct not atomic)

    private readonly ILogger _logger;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ConcurrentDictionary<uint, (MonitoredItem monitoredItem, RegisteredSubjectProperty property)> _monitoredItems = new();
    private readonly SemaphoreSlim _applyChangesLock = new(1, 1); // Coordinates concurrent ApplyChanges calls

    private volatile PollingManager? _pollingManager;
    private ImmutableArray<Subscription> _subscriptions = ImmutableArray<Subscription>.Empty;
    private volatile ISubjectUpdater? _updater;
    private volatile bool _shuttingDown; // Prevents new callbacks during cleanup

    /// <summary>
    /// Gets the current list of subscriptions.
    /// Thread-safe using lock to prevent torn reads of ImmutableArray struct.
    /// </summary>
    public IReadOnlyList<Subscription> Subscriptions
    {
        get
        {
            lock (_subscriptionsLock)
            {
                return _subscriptions;
            }
        }
    }

    public ConcurrentDictionary<uint, (MonitoredItem monitoredItem, RegisteredSubjectProperty property)> MonitoredItems => _monitoredItems;

    public OpcUaSubscriptionManager(OpcUaClientConfiguration configuration, ILogger logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public void SetUpdater(ISubjectUpdater updater)
    {
        _updater = updater;
    }

    public void SetPollingManager(PollingManager pollingManager)
    {
        _pollingManager = pollingManager;
    }

    public void Clear()
    {
        _monitoredItems.Clear();
        lock (_subscriptionsLock)
        {
            _subscriptions = ImmutableArray<Subscription>.Empty;
        }
    }

    public async Task CreateBatchedSubscriptionsAsync(
        IReadOnlyList<MonitoredItem> monitoredItems,
        Session session,
        CancellationToken cancellationToken)
    {
        _shuttingDown = false;
        
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
                    _monitoredItems[item.ClientHandle] = (item, p);
                }
            }

            try
            {
                // Coordinate with health monitor to prevent concurrent ApplyChanges
                await _applyChangesLock.WaitAsync(cancellationToken);
                try
                {
                    await subscription.ApplyChangesAsync(cancellationToken);
                }
                finally
                {
                    _applyChangesLock.Release();
                }
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
        }

        // Replace subscriptions array with lock to ensure atomic assignment
        // ImmutableArray is a struct - assignment not atomic on all platforms
        var newSubscriptions = builder.ToImmutable();
        lock (_subscriptionsLock)
        {
            _subscriptions = newSubscriptions;
        }
    }

    /// <summary>
    /// Updates the subscription list to reference subscriptions transferred by SessionReconnectHandler.
    /// Called after successful session transfer to embrace OPC Foundation's subscription preservation.
    /// </summary>
    public void UpdateTransferredSubscriptions(ImmutableArray<Subscription> transferredSubscriptions)
    {
        foreach (var subscription in transferredSubscriptions)
        {
            subscription.FastDataChangeCallback -= OnFastDataChange;
            subscription.FastDataChangeCallback += OnFastDataChange;
        }

        lock (_subscriptionsLock)
        {
            _subscriptions = transferredSubscriptions;
        }

        _logger.LogInformation("Updated subscription manager with {Count} transferred subscriptions", transferredSubscriptions.Length);
    }
    
    private void OnFastDataChange(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        // Thread-safety: This callback is invoked sequentially per subscription (OPC UA stack guarantee).
        // Multiple subscriptions can invoke callbacks concurrently, but each subscription manages
        // distinct monitored items (no overlap), so concurrent callbacks won't interfere.
        // The EnqueueOrApplyUpdate ensures changes are applied/enqueued in order.

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
            if (_monitoredItems.TryGetValue(item.ClientHandle, out var tuple))
            {
                changes.Add(new OpcUaPropertyUpdate
                {
                    Property = tuple.property,
                    Value = _configuration.ValueConverter.ConvertToPropertyValue(item.Value.Value, tuple.property),
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
                // Coordinate with health monitor to prevent concurrent ApplyChanges
                await _applyChangesLock.WaitAsync(cancellationToken);
                try
                {
                    await subscription.ApplyChangesAsync(cancellationToken);
                }
                finally
                {
                    _applyChangesLock.Release();
                }
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
        // Note: BadAttributeIdInvalid is a permanent error - polling won't work either, so excluded
        return statusCode == StatusCodes.BadNotSupported ||
               statusCode == StatusCodes.BadMonitoredItemFilterUnsupported;
    }

    public void Cleanup()
    {
        _shuttingDown = true;

        ImmutableArray<Subscription> subscriptions;
        lock (_subscriptionsLock)
        {
            subscriptions = _subscriptions;
            _subscriptions = ImmutableArray<Subscription>.Empty;
        }

        foreach (var subscription in subscriptions)
        {
            subscription.FastDataChangeCallback -= OnFastDataChange;
            subscription.Delete(true);
        }
    }

    public void Dispose()
    {
        _applyChangesLock.Dispose();
        Cleanup();
    }
}
