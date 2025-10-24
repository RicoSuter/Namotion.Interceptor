using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;
using System.Collections.Concurrent;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubscriptionManager
{
    private readonly ILogger _logger;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ConcurrentDictionary<uint, RegisteredSubjectProperty> _monitoredItems = new();
    private readonly List<Subscription> _activeSubscriptions = [];

    private ISubjectMutationDispatcher? _dispatcher;

    public OpcUaSubscriptionManager(
        OpcUaClientConfiguration configuration,
        ILogger logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public void SetDispatcher(ISubjectMutationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
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
                PublishingInterval = 0, // TODO: Set to a reasonable value
                DisableMonitoredItemCache = true,
                MinLifetimeInterval = 60_000,
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
                _logger.LogWarning(sre, "ApplyChanges failed for a batch; attempting to keep valid monitored items by removing failed ones.");
            }

            var removed = await FilterOutFailedMonitoredItemsAsync(subscription, cancellationToken);
            if (removed > 0)
            {
                _logger.LogWarning("Removed {Removed} monitored items that failed to create in subscription {Id}.", removed, subscription.Id);
            }
            
            _logger.LogInformation("Created subscription {SubscriptionId} with {Count} monitored items.", subscription.Id, subscription.MonitoredItems.Count());
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

                _logger.LogError("Monitored item creation failed for {DisplayName} (Handle={Handle}): {Status}", 
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
                _logger.LogWarning(ex, "ApplyChanges after removing failed items still failed. Continuing with remaining items.");
            }

            return itemsToRemove.Count;
        }

        return 0;
    }

    private void OnFastDataChange(Subscription subscription, DataChangeNotification notification, IList<string> stringtable)
    {
        if (_dispatcher is null)
        {
            return;
        }
        
        var monitoredItemsCount = notification.MonitoredItems.Count;
        if (monitoredItemsCount == 0)
        {
            return;
        }

        var receivedTimestamp = DateTimeOffset.Now;

        var changes = new List<OpcUaPropertyUpdate>(monitoredItemsCount);
        for (var i = 0; i < monitoredItemsCount; i++)
        {
            var item = notification.MonitoredItems[i];
            if (_monitoredItems.TryGetValue(item.ClientHandle, out var property))
            {
                changes.Add(new OpcUaPropertyUpdate
                {
                    Property = property,
                    Value = _configuration.ValueConverter.ConvertToPropertyValue(item.Value.Value, property.Type),
                    Timestamp = item.Value.SourceTimestamp
                });
            }
        }
        
        if (changes.Count == 0)
        {
            return;
        }
        
        _dispatcher?.EnqueueSubjectUpdate(() =>
        {
            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                try
                {
                    change.Property.SetValueFromSource(subscription, change.Timestamp, receivedTimestamp, change.Value);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to apply change for {Path}.", change.Property.Name);
                }
            }
        });
    }

    private struct OpcUaPropertyUpdate
    {
        public PropertyReference Property { get; init; }
        
        public DateTimeOffset Timestamp { get; init; }
        
        public object? Value { get; init; }
    }
}
