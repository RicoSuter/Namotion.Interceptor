using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client.Subscriptions;
using Opc.Ua.Client.Subscriptions.MonitoredItems;

namespace Namotion.Interceptor.OpcUa.Client.Resilience;

/// <summary>
/// Monitors OPC UA subscription health and retries failed monitored items. The subscription engine
/// retries a failed item a bounded number of times by itself; this requeues the retryable ones that
/// are still failing afterwards, on every health check.
/// </summary>
internal sealed class SubscriptionHealthMonitor
{
    private readonly ILogger _logger;
    private readonly Action<Exception> _reportError;

    public SubscriptionHealthMonitor(ILogger logger, Action<Exception> reportError)
    {
        _logger = logger;
        _reportError = reportError;
    }

    public void CheckAndHealSubscriptions(IReadOnlyCollection<ISubscription> subscriptions, CancellationToken cancellationToken)
    {
        foreach (var subscription in subscriptions)
        {
            try
            {
                CheckAndHealSubscription(subscription);
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _reportError(ex);
                }

                _logger.LogError(ex, "OPC UA subscription health check failed.");
            }
        }
    }

    private void CheckAndHealSubscription(ISubscription subscription)
    {
        var unhealthyCount = 0;
        var requeuedCount = 0;
        var retryCollection = subscription.MonitoredItems as IMonitoredItemRetryCollection;

        foreach (var item in subscription.MonitoredItems.Items)
        {
            if (!IsUnhealthy(item) || !IsRetryable(item))
            {
                continue;
            }

            unhealthyCount++;

            // Returns false while the engine is still applying the item by itself.
            if (retryCollection is not null && retryCollection.TryRequeue(item.ClientHandle))
            {
                requeuedCount++;
            }
        }

        if (unhealthyCount > 0)
        {
            _logger.LogWarning(
                "OPC UA subscription has {Unhealthy} failed monitored items that can be retried; requeued {Requeued}.",
                unhealthyCount, requeuedCount);
        }
    }

    /// <summary>
    /// Determines if a monitored item is unhealthy (not created or has bad status).
    /// </summary>
    internal static bool IsUnhealthy(IMonitoredItem item)
    {
        return !item.Created || ServiceResult.IsBad(item.Error);
    }

    /// <summary>
    /// Determines if a failed monitored item should be retried.
    /// Returns false for permanent design-time errors, true for transient errors.
    /// </summary>
    internal static bool IsRetryable(IMonitoredItem item)
    {
        var statusCode = item.Error?.StatusCode ?? StatusCodes.Good;
        return OpcUaStatusCodeClassifier.IsTransientError(statusCode);
    }
}
