using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;
using System.Collections.Concurrent;
using Namotion.Interceptor.Registry.Performance;
using Namotion.Interceptor.Sources;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubscriptionManager
{
    private readonly ILogger _logger;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ConcurrentDictionary<uint, RegisteredSubjectProperty> _monitoredItems = new();
    private readonly List<Subscription> _activeSubscriptions = [];

    private ISubjectUpdater? _updater;

    public OpcUaSubscriptionManager(
        OpcUaClientConfiguration configuration,
        ILogger logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public void SetUpdater(ISubjectUpdater updater)
    {
        _updater = updater;
    }

    public void Clear()
    {
        _monitoredItems.Clear();
        _activeSubscriptions.Clear();
    }

    public async Task CreateBatchedSubscriptionsAsync(
        IReadOnlyList<MonitoredItem> monitoredItems,
        Session session,
        CancellationToken cancellationToken)
    {
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
            _activeSubscriptions.Add(subscription);

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
    }

    public void Cleanup()
    {
        foreach (var subscription in _activeSubscriptions)
        {
            try
            {
                subscription.FastDataChangeCallback -= OnFastDataChange;
                subscription.Delete(true);
            }
            catch { /* ignore cleanup exceptions */ }
        }
        _activeSubscriptions.Clear();
    }

    private async Task<int> FilterOutFailedMonitoredItemsAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        List<MonitoredItem>? itemsToRemove = null;
        
        foreach (var monitoredItem in subscription.MonitoredItems)
        {
            var statusCode = monitoredItem.Status?.Error?.StatusCode ?? StatusCodes.Good;
            var hasFailed = !monitoredItem.Created || StatusCode.IsBad(statusCode);
            if (hasFailed)
            {
                itemsToRemove ??= [];
                itemsToRemove.Add(monitoredItem);

                _monitoredItems.TryRemove(monitoredItem.ClientHandle, out _);

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
        var state = (source: this, subscription, receivedTimestamp, changes: notification.MonitoredItems);
        _updater?.EnqueueOrApplyUpdate(state, static s =>
        {
            Parallel.ForEach(s.changes, item =>
            {
                if (s.source._monitoredItems.TryGetValue(item.ClientHandle, out var property))
                {
                    try
                    {
                        var value = s.source._configuration
                            .ValueConverter
                            .ConvertToPropertyValue(item.Value.Value, property);

                        property.Reference.SetValueFromSource(
                            s.source, item.Value.SourceTimestamp, s.receivedTimestamp, value);
                    }
                    catch (Exception e)
                    {
                        s.source._logger.LogError(e, "Failed to apply change for OPC UA {Path}.", property.Reference.Name);
                    }
                }
            });
        });
    }
}
