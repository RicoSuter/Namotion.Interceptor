using Microsoft.Extensions.Configuration;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Connectors.TwinCAT.Client;

/// <summary>
/// Configuration for the TwinCAT ADS client source.
/// </summary>
public class AdsClientConfiguration
{
    /// <summary>
    /// Gets or sets the PLC host IP or hostname.
    /// </summary>
    public required string Host { get; set; }

    /// <summary>
    /// Gets or sets the AMS Net ID of the PLC (e.g., "192.168.1.100.1.1").
    /// </summary>
    public required string AmsNetId { get; set; }

    /// <summary>
    /// Gets or sets the AMS port (default: 851 for TwinCAT3 PLC runtime).
    /// </summary>
    public int AmsPort { get; set; } = 851;

    /// <summary>
    /// Gets or sets the ADS communication timeout.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the path provider for property-to-symbol mapping.
    /// </summary>
    public required IPathProvider PathProvider { get; set; }

    /// <summary>
    /// Gets or sets the default read mode for variables without explicit configuration.
    /// </summary>
    public AdsReadMode DefaultReadMode { get; set; } = AdsReadMode.Auto;

    /// <summary>
    /// Gets or sets the default notification cycle time in milliseconds.
    /// </summary>
    public int DefaultCycleTime { get; set; } = 100;

    /// <summary>
    /// Gets or sets the default max delay for notification batching in milliseconds.
    /// </summary>
    public int DefaultMaxDelay { get; set; } = 0;

    /// <summary>
    /// Gets or sets the maximum number of concurrent ADS notifications before demotion.
    /// </summary>
    public int MaxNotifications { get; set; } = 500;

    /// <summary>
    /// Gets or sets the polling interval for polled/demoted variables.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or sets the write retry queue size.
    /// </summary>
    public int WriteRetryQueueSize { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the health check interval for monitoring.
    /// </summary>
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the debounce time for rescan requests.
    /// When multiple events (connection restored, PLC state change, symbol version change) fire
    /// in rapid succession, the rescan waits until this time has elapsed since the last request
    /// before executing, coalescing multiple events into a single rescan.
    /// </summary>
    public TimeSpan RescanDebounceTime { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the buffer time for batching inbound updates.
    /// </summary>
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Gets or sets the retry time for failed writes.
    /// </summary>
    public TimeSpan RetryTime { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the circuit breaker failure threshold.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>
    /// Gets or sets the circuit breaker cooldown period.
    /// </summary>
    public TimeSpan CircuitBreakerCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the value converter for ADS/PLC type conversions.
    /// </summary>
    public AdsValueConverter ValueConverter { get; set; } = new();

    /// <summary>
    /// Gets or sets the optional router configuration for the ADS client.
    /// When set, the ADS client uses a custom loopback port (useful for testing without TwinCAT installed).
    /// When null, the default ADS client behavior is used.
    /// </summary>
    public IConfiguration? RouterConfiguration { get; set; }

    /// <summary>
    /// Validates the configuration and throws if invalid.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("Host must not be empty.", nameof(Host));

        if (string.IsNullOrWhiteSpace(AmsNetId))
            throw new ArgumentException("AmsNetId must not be empty.", nameof(AmsNetId));

        if (AmsPort <= 0)
            throw new ArgumentOutOfRangeException(nameof(AmsPort), "AmsPort must be positive.");

        if (Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Timeout), "Timeout must be positive.");

        if (DefaultCycleTime <= 0)
            throw new ArgumentOutOfRangeException(nameof(DefaultCycleTime), "DefaultCycleTime must be positive.");

        if (MaxNotifications <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxNotifications), "MaxNotifications must be positive.");

        if (PollingInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PollingInterval), "PollingInterval must be positive.");

        if (WriteRetryQueueSize < 0)
            throw new ArgumentOutOfRangeException(nameof(WriteRetryQueueSize), "WriteRetryQueueSize must be non-negative.");

        if (HealthCheckInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(HealthCheckInterval), "HealthCheckInterval must be positive.");

        if (CircuitBreakerFailureThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(CircuitBreakerFailureThreshold), "CircuitBreakerFailureThreshold must be positive.");

        if (CircuitBreakerCooldown <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(CircuitBreakerCooldown), "CircuitBreakerCooldown must be positive.");

        if (DefaultMaxDelay < 0)
            throw new ArgumentOutOfRangeException(nameof(DefaultMaxDelay), "DefaultMaxDelay must be non-negative.");

        if (RescanDebounceTime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RescanDebounceTime), "RescanDebounceTime must be non-negative.");
    }
}
