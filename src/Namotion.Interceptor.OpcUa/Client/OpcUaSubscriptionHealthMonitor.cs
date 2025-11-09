using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Monitors OPC UA subscription health and automatically retries failed monitored items.
/// Periodically checks for unhealthy items and attempts to heal them by calling ApplyChanges.
/// </summary>
internal sealed class OpcUaSubscriptionHealthMonitor
{
    private readonly ILogger _logger;

    public OpcUaSubscriptionHealthMonitor(ILogger logger)
    {
        _logger = logger;
    }

    public async Task CheckAndHealSubscriptionsAsync(IReadOnlyList<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var subscription in subscriptions)
            {
                var unhealthyCount = GetUnhealthyCount(subscription);
                if (unhealthyCount == 0)
                {
                    continue;
                }

                try
                {
                    await subscription.ApplyChangesAsync(cancellationToken);

                    var stillUnhealthyCount = GetUnhealthyCount(subscription);
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

    private static int GetUnhealthyCount(Subscription subscription)
    {
        return subscription
            .MonitoredItems
            .Count(monitoredItem => IsUnhealthy(monitoredItem) && IsRetryable(monitoredItem));
    }

    /// <summary>
    /// Determines if a monitored item is unhealthy (not created or has bad status).
    /// </summary>
    internal static bool IsUnhealthy(MonitoredItem item)
    {
        var statusCode = item.Status?.Error?.StatusCode ?? StatusCodes.Good;
        return !item.Created || StatusCode.IsBad(statusCode);
    }

    /// <summary>
    /// Determines if a failed monitored item should be retried.
    /// Returns false for permanent design-time errors, true for transient errors.
    /// </summary>
    internal static bool IsRetryable(MonitoredItem item)
    {
        var statusCode = item.Status?.Error?.StatusCode ?? StatusCodes.Good;

        // Design-time errors - don't retry (permanent errors)
        if (statusCode == StatusCodes.BadNodeIdUnknown ||
            statusCode == StatusCodes.BadAttributeIdInvalid ||
            statusCode == StatusCodes.BadIndexRangeInvalid)
        {
            return false;
        }

        // Retryable transient errors (e.g., BadTooManyMonitoredItems, BadOutOfService)
        // Retry any bad status that's not a permanent design-time error
        return StatusCode.IsBad(statusCode);
    }
}
