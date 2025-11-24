using System;
using MQTTnet.Protocol;
using Namotion.Interceptor.Sources.Paths;

namespace Namotion.Interceptor.Mqtt.Client;

/// <summary>
/// Configuration for MQTT client source.
/// </summary>
public class MqttClientConfiguration
{
    /// <summary>
    /// Gets or sets the MQTT broker hostname or IP address.
    /// </summary>
    public required string BrokerHost { get; init; }

    /// <summary>
    /// Gets or sets the MQTT broker port. Default is 1883.
    /// </summary>
    public int BrokerPort { get; init; } = 1883;

    /// <summary>
    /// Gets or sets the username for broker authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Gets or sets the password for broker authentication.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Gets or sets whether to use TLS/SSL for the connection. Default is false.
    /// </summary>
    public bool UseTls { get; init; }

    /// <summary>
    /// Gets or sets the client identifier. Default is a unique GUID-based identifier.
    /// </summary>
    public string ClientId { get; init; } = $"Namotion_{Guid.NewGuid():N}";

    /// <summary>
    /// Gets or sets whether to use a clean session. Default is true.
    /// When false, the broker preserves subscriptions and queued messages across reconnects.
    /// </summary>
    public bool CleanSession { get; init; } = true;

    /// <summary>
    /// Gets or sets the optional topic prefix. When set, all topics are prefixed with this value.
    /// </summary>
    public string? TopicPrefix { get; init; }

    /// <summary>
    /// Gets or sets the source path provider for property-to-topic mapping.
    /// </summary>
    public required ISourcePathProvider PathProvider { get; init; }

    // QoS settings

    /// <summary>
    /// Gets or sets the default QoS level for publish/subscribe operations. Default is AtMostOnce (0) for high throughput.
    /// </summary>
    public MqttQualityOfServiceLevel DefaultQualityOfService { get; init; } = MqttQualityOfServiceLevel.AtMostOnce;

    /// <summary>
    /// Gets or sets whether to use retained messages. Default is true.
    /// Retained messages enable initial state loading.
    /// </summary>
    public bool UseRetainedMessages { get; init; } = true;
    
    /// <summary>
    /// Gets or sets the connection timeout. Default is 10 seconds.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the initial delay before attempting to reconnect. Default is 2 seconds.
    /// </summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the maximum delay between reconnection attempts (for exponential backoff). Default is 60 seconds.
    /// </summary>
    public TimeSpan MaximumReconnectDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the keep-alive interval. Default is 15 seconds.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the interval for connection health checks. Default is 30 seconds.
    /// Health checks use TryPingAsync to verify the connection is still alive.
    /// </summary>
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// Gets or sets the time to buffer property changes before sending. Default is 8ms.
    /// </summary>
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Gets or sets the time between retry attempts for failed writes. Default is 10 seconds.
    /// </summary>
    public TimeSpan RetryTime { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the maximum number of writes to queue during disconnection. Default is 1000.
    /// Set to 0 to disable write buffering.
    /// </summary>
    public int WriteRetryQueueSize { get; init; } = 1000;
    
    /// <summary>
    /// Gets or sets the value converter for serialization/deserialization. Default is JSON.
    /// </summary>
    public IMqttValueConverter ValueConverter { get; init; } = new JsonMqttValueConverter();
    
    /// <summary>
    /// Gets or sets the MQTT user property name for the source timestamp. Default is "ts".
    /// Set to null to disable timestamp extraction.
    /// </summary>
    public string? SourceTimestampPropertyName { get; init; } = "ts";

    /// <summary>
    /// Validates the configuration and throws if invalid.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when configuration is invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BrokerHost))
        {
            throw new ArgumentException("BrokerHost must be specified.", nameof(BrokerHost));
        }

        if (BrokerPort is < 1 or > 65535)
        {
            throw new ArgumentException($"BrokerPort must be between 1 and 65535, got: {BrokerPort}", nameof(BrokerPort));
        }

        if (PathProvider is null)
        {
            throw new ArgumentException("PathProvider must be specified.", nameof(PathProvider));
        }

        if (ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException($"ConnectTimeout must be positive, got: {ConnectTimeout}", nameof(ConnectTimeout));
        }

        if (ReconnectDelay <= TimeSpan.Zero)
        {
            throw new ArgumentException($"ReconnectDelay must be positive, got: {ReconnectDelay}", nameof(ReconnectDelay));
        }

        if (MaximumReconnectDelay < ReconnectDelay)
        {
            throw new ArgumentException($"MaxReconnectDelay must be >= ReconnectDelay, got: {MaximumReconnectDelay}", nameof(MaximumReconnectDelay));
        }

        if (WriteRetryQueueSize < 0)
        {
            throw new ArgumentException($"WriteRetryQueueSize must be non-negative, got: {WriteRetryQueueSize}", nameof(WriteRetryQueueSize));
        }

        if (ValueConverter is null)
        {
            throw new ArgumentException("ValueConverter must be specified.", nameof(ValueConverter));
        }
    }
}
