using System;
using MQTTnet.Protocol;
using Namotion.Interceptor.Sources.Paths;

namespace Namotion.Interceptor.Mqtt.Server;

/// <summary>
/// Configuration for MQTT server (publisher) background service.
/// </summary>
public class MqttServerConfiguration
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
    /// Gets or sets the client identifier.
    /// </summary>
    public string ClientId { get; init; } = $"Namotion_{Guid.NewGuid():N}";

    /// <summary>
    /// Gets or sets the optional topic prefix.
    /// </summary>
    public string? TopicPrefix { get; init; }

    /// <summary>
    /// Gets or sets the source path provider for property-to-topic mapping.
    /// </summary>
    public required ISourcePathProvider PathProvider { get; init; }

    /// <summary>
    /// Gets or sets the default QoS level. Default is AtMostOnce (0) for high throughput.
    /// </summary>
    public MqttQualityOfServiceLevel DefaultQualityOfService { get; init; } = MqttQualityOfServiceLevel.AtMostOnce;

    /// <summary>
    /// Gets or sets whether to use retained messages. Default is true.
    /// </summary>
    public bool UseRetainedMessages { get; init; } = true;

    /// <summary>
    /// Gets or sets the maximum pending messages per client. Default is 10000.
    /// Messages are dropped when the queue exceeds this limit.
    /// </summary>
    public int MaxPendingMessagesPerClient { get; init; } = 10000;

    /// <summary>
    /// Gets or sets the time to buffer property changes before sending. Default is 8ms.
    /// </summary>
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Gets or sets the value converter. Default is JSON.
    /// </summary>
    public IMqttValueConverter ValueConverter { get; init; } = new JsonMqttValueConverter();

    /// <summary>
    /// Gets or sets the MQTT user property name for the source timestamp. Default is "ts".
    /// Set to null to disable timestamp inclusion.
    /// </summary>
    public string? SourceTimestampPropertyName { get; init; } = "ts";

    /// <summary>
    /// Validates the configuration.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BrokerHost))
        {
            throw new ArgumentException("BrokerHost must be specified.", nameof(BrokerHost));
        }

        if (PathProvider is null)
        {
            throw new ArgumentException("PathProvider must be specified.", nameof(PathProvider));
        }
    }
}
