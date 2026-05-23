using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.Resilience;

/// <summary>
/// Monitors OPC UA subscription health and automatically retries failed monitored items.
/// Periodically checks for unhealthy items and attempts to heal them by calling ApplyChanges.
/// </summary>
internal sealed class SubscriptionHealthMonitor
{
    private readonly ILogger _logger;

    public SubscriptionHealthMonitor(ILogger logger)
    {
        _logger = logger;
    }

    public async Task CheckAndHealSubscriptionsAsync(IReadOnlyCollection<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var subscription in subscriptions)
            {
                var unhealthyCount = GetTransientErrorCount(subscription);
                if (unhealthyCount == 0)
                {
                    continue;
                }

                try
                {
                    // Try to heal failed monitored items by reapplying the subscription changes
                    await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);

                    var stillUnhealthyCount = GetTransientErrorCount(subscription);
                    if (stillUnhealthyCount == 0)
                    {
                        _logger.LogInformation(
                            "OPC UA subscription {Id} healed successfully: All {Count} items now healthy.",
                            subscription.Id, unhealthyCount);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "OPC UA subscription {Id} healed partially: {Healthy}/{Total} items recovered.",
                            subscription.Id, unhealthyCount - stillUnhealthyCount, unhealthyCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to heal OPC UA subscription {Id}.", subscription.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPC UA subscription health check failed.");
        }
    }

    private static int GetTransientErrorCount(Subscription subscription)
    {
        var count = 0;
        foreach (var item in subscription.MonitoredItems)
        {
            var statusCode = item.Status?.Error?.StatusCode ?? StatusCodes.Good;
            if (OpcUaStatusCodeClassifier.IsTransient(statusCode))
            {
                count++;
            }
        }
        return count;
    }
}
