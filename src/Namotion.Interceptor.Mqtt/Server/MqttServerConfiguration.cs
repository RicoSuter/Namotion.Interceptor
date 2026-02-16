using System;
using System.Buffers.Text;
using MQTTnet.Protocol;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mqtt.Server;

/// <summary>
/// Configuration for MQTT server (publisher) background service.
/// </summary>
public class MqttServerConfiguration
{
    /// <summary>
    /// Gets or sets the MQTT broker hostname or IP address to bind to.
    /// Use "localhost" to bind to loopback only, or an IP address to bind to a specific interface.
    /// Default is null which binds to all interfaces.
    /// </summary>
    public string? BrokerHost { get; init; }

    /// <summary>
    /// Gets or sets the MQTT broker port. Default is 1883.
    /// </summary>
    public int BrokerPort { get; init; } = 1883;

    /// <summary>
    /// Gets or sets the client identifier.
    /// </summary>
    public string ClientId { get; init; } = $"Namotion_{Guid.NewGuid():N}";

    /// <summary>
    /// Gets or sets the optional topic prefix.
    /// </summary>
    public string? TopicPrefix { get; init; }

    /// <summary>
    /// Gets or sets the path provider for property-to-topic mapping.
    /// </summary>
    public required PathProviderBase PathProvider { get; init; }

    /// <summary>
    /// Gets or sets the default QoS level. Default is AtLeastOnce (1) for guaranteed delivery.
    /// Use AtMostOnce (0) only when message loss is acceptable and lowest latency is required.
    /// </summary>
    public MqttQualityOfServiceLevel DefaultQualityOfService { get; init; } = MqttQualityOfServiceLevel.AtLeastOnce;

    /// <summary>
    /// Gets or sets whether to use retained messages. Default is true so new subscribers receive
    /// the last known value. Disable for slightly better throughput if not needed.
    /// </summary>
    public bool UseRetainedMessages { get; init; } = true;

    /// <summary>
    /// Gets or sets the maximum pending messages per client. Default is 25000.
    /// Messages are dropped when the queue exceeds this limit.
    /// </summary>
    public int MaxPendingMessagesPerClient { get; init; } = 25000;

    /// <summary>
    /// Gets or sets the time to buffer property changes before sending. Default is 8ms.
    /// Higher values create larger batches for better throughput, lower values reduce latency.
    /// </summary>
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Gets or sets the delay before publishing the initial state to a newly connected client.
    /// This allows time for the client to complete its subscription setup.
    /// Set to zero to disable initial state publishing (relies on retained messages only).
    /// Default is 500ms.
    /// </summary>
    public TimeSpan InitialStateDelay { get; init; } = TimeSpan.FromMilliseconds(500);

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
    /// Gets or sets the function for serializing timestamps to bytes for MQTT user properties.
    /// Default converts to Unix milliseconds as UTF8. Must be paired with a matching <see cref="SourceTimestampDeserializer"/>.
    /// </summary>
    public Func<DateTimeOffset, byte[]> SourceTimestampSerializer { get; init; } =
        static timestamp =>
        {
            Span<byte> buffer = stackalloc byte[20];
            Utf8Formatter.TryFormat(timestamp.ToUnixTimeMilliseconds(), buffer, out var bytesWritten);
            return buffer[..bytesWritten].ToArray();
        };

    /// <summary>
    /// Gets or sets the function for deserializing timestamp bytes from MQTT user properties.
    /// Default parses Unix milliseconds from UTF8. Must be paired with a matching <see cref="SourceTimestampSerializer"/>.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, DateTimeOffset?> SourceTimestampDeserializer { get; init; } =
        static value => Utf8Parser.TryParse(value.Span, out long unixMs, out _)
            ? DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
            : null;

    /// <summary>
    /// Validates the configuration.
    /// </summary>
    public void Validate()
    {
        if (PathProvider is null)
        {
            throw new ArgumentException("PathProvider must be specified.", nameof(PathProvider));
        }
    }
}
