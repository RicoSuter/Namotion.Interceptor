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

    private ImmutableArray<Subscription> _subscriptions = ImmutableArray<Subscription>.Empty;
    private ISubjectUpdater? _updater;

    /// <summary>
    /// Gets the current list of subscriptions. Lock-free and allocation-free when accessed.
    /// Returns the immutable array directly - no copying or locking required.
    /// </summary>
    public IReadOnlyList<Subscription> Subscriptions => _subscriptions;

    public int TotalMonitoredItemCount => _monitoredItems.Count;

    public OpcUaSubscriptionManager(
        OpcUaClientConfiguration configuration,
        ILogger logger)
    {
        _configuration = configuration;
        _logger = logger;

        _healthMonitor = new OpcUaSubscriptionHealthMonitor(configuration, this, logger);
    }

    public void SetUpdater(ISubjectUpdater updater)
    {
        _updater = updater;
    }

    public void Clear()
    {
        _monitoredItems.Clear();
        _subscriptions = ImmutableArray<Subscription>.Empty;
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

            var removed = await FilterOutFailedMonitoredItemsAsync(subscription, cancellationToken);
            if (removed > 0)
            {
                // TODO: Switch to polling when monitoring failed
                _logger.LogWarning("Removed {Removed} monitored items that failed to create in OPC UA subscription {SubscriptionId}.", removed, subscription.Id);
            }

            _logger.LogInformation("Created OPC UA subscription {SubscriptionId} with {Count} monitored items.", subscription.Id, subscription.MonitoredItems.Count());
        }

        // Atomically replace the subscriptions array with memory barriers to ensure visibility
        var newSubscriptions = builder.ToImmutable();
        Interlocked.MemoryBarrier();
        _subscriptions = newSubscriptions;
        Interlocked.MemoryBarrier();
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

        // Atomically update subscription reference with memory barrier
        var newSubscriptions = builder.ToImmutable();
        Interlocked.MemoryBarrier();
        _subscriptions = newSubscriptions;
        Interlocked.MemoryBarrier();

        _logger.LogInformation("Updated subscription manager with {Count} transferred subscriptions", subscriptionList.Count);
    }
    
    private void OnFastDataChange(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        // This callback is called in sequence/in order per subscription.
        // The EnqueueOrApplyUpdate we ensure that changes either applied in order directly
        // or enqueued in the same order (and thus no synchronization is needed here).
        
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
    
    private async Task<int> FilterOutFailedMonitoredItemsAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        List<MonitoredItem>? itemsToRemove = null;

        foreach (var monitoredItem in subscription.MonitoredItems)
        {
            if (OpcUaSubscriptionHealthMonitor.IsUnhealthy(monitoredItem))
            {
                itemsToRemove ??= [];
                itemsToRemove.Add(monitoredItem);

                _monitoredItems.TryRemove(monitoredItem.ClientHandle, out _);

                var statusCode = monitoredItem.Status?.Error?.StatusCode ?? StatusCodes.Good;
                _logger.LogError("OPC UA monitored item creation failed for {DisplayName} (Handle={Handle}): {Status}",
                    monitoredItem.DisplayName, monitoredItem.ClientHandle, statusCode);
            }
        }

        if (itemsToRemove?.Count > 0)
        {
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

            return itemsToRemove.Count;
        }

        return 0;
    }

    public void Cleanup()
    {
        _healthMonitor.Stop();

        // Capture current subscriptions and atomically clear (lock-free)
        var subscriptions = _subscriptions;
        _subscriptions = ImmutableArray<Subscription>.Empty;

        // Clean up captured subscriptions
        foreach (var subscription in subscriptions)
        {
            try
            {
                subscription.FastDataChangeCallback -= OnFastDataChange;
                subscription.Delete(true);
            }
            catch { /* ignore cleanup exceptions */ }
        }
    }

    public void Dispose()
    {
        _healthMonitor.Dispose();
        Cleanup();
    }

    private struct OpcUaPropertyUpdate
    {
        public PropertyReference Property { get; init; }

        public DateTimeOffset Timestamp { get; init; }

        public object? Value { get; init; }
    }
}
