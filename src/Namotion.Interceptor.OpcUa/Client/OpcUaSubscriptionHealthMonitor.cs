using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Monitors OPC UA subscription health and automatically retries failed monitored items.
/// Periodically checks for unhealthy items and attempts to heal them by calling ApplyChanges.
/// </summary>
internal sealed class OpcUaSubscriptionHealthMonitor : IDisposable
{
    private readonly ILogger _logger;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubscriptionManager _subscriptionManager;
    private readonly ManualResetEventSlim _healthCheckInProgress = new(true); // true = not in progress
    private IDisposable? _healthCheckTimer;

    public OpcUaSubscriptionHealthMonitor(
        OpcUaClientConfiguration configuration,
        OpcUaSubscriptionManager subscriptionManager,
        ILogger logger)
    {
        _configuration = configuration;
        _subscriptionManager = subscriptionManager;
        _logger = logger;
    }

    /// <summary>
    /// Starts the periodic health monitoring timer.
    /// </summary>
    public void Start()
    {
        if (!_configuration.EnableAutoHealing)
        {
            _logger.LogInformation("Auto-healing of failed monitored items is disabled");
            return;
        }

        _logger.LogInformation(
            "Starting subscription health monitoring (interval: {Interval})",
            _configuration.SubscriptionHealthCheckInterval);

        _healthCheckTimer = Observable
            .Timer(TimeSpan.FromSeconds(5), _configuration.SubscriptionHealthCheckInterval)
            .Subscribe(_ =>
            {
                _healthCheckInProgress.Reset(); // Signal: callback started
                try
                {
                    CheckAndHealSubscriptions();
                }
                finally
                {
                    _healthCheckInProgress.Set(); // Signal: callback complete
                }
            });
    }

    /// <summary>
    /// Stops the health monitoring timer and waits for in-flight checks to complete.
    /// </summary>
    public void Stop()
    {
        var timer = _healthCheckTimer;
        if (timer != null)
        {
            timer.Dispose(); // Stop new callbacks
            _healthCheckTimer = null;

            // Wait for in-flight callback with timeout
            if (!_healthCheckInProgress.Wait(TimeSpan.FromSeconds(5)))
            {
                _logger.LogWarning("Health check did not complete within timeout during shutdown");
            }
        }
    }

    private void CheckAndHealSubscriptions()
    {
        try
        {
            var subscriptions = _subscriptionManager.Subscriptions;
            foreach (var subscription in subscriptions)
            {
                // Count unhealthy items first to avoid allocation in common case (all healthy)
                var unhealthyCount = 0;
                foreach (var mi in subscription.MonitoredItems)
                {
                    if (IsUnhealthy(mi) && IsRetryable(mi))
                    {
                        unhealthyCount++;
                    }
                }

                if (unhealthyCount == 0)
                {
                    continue; // Fast path - no allocation
                }

                // Allocate list only when needed
                var unhealthyItems = new List<MonitoredItem>(unhealthyCount);
                foreach (var mi in subscription.MonitoredItems)
                {
                    if (IsUnhealthy(mi) && IsRetryable(mi))
                    {
                        unhealthyItems.Add(mi);
                    }
                }

                _logger.LogWarning(
                    "Found {Count} unhealthy retryable items in OPC UA subscription {Id}. Attempting to heal...",
                    unhealthyItems.Count, subscription.Id);

                try
                {
                    subscription.ApplyChanges(); // Triggers re-creation

                    // Check if healing succeeded
                    var stillUnhealthy = unhealthyItems.Count(IsUnhealthy);
                    if (stillUnhealthy == 0)
                    {
                        _logger.LogInformation(
                            "OPC UA subscription {Id} healed successfully - all {Count} items now healthy.",
                            subscription.Id, unhealthyItems.Count);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "OPC UA subscription {Id} partial healing - {Healthy}/{Total} items recovered. Will retry.",
                            subscription.Id, unhealthyItems.Count - stillUnhealthy, unhealthyItems.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to heal OPC UA subscription {Id}.", subscription.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPC UA subscription health check failed.");
        }
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

    public void Dispose()
    {
        Stop();
        _healthCheckInProgress.Dispose();
    }
}
