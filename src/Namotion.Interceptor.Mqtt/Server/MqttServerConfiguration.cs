using System;
using MQTTnet.Protocol;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mqtt.Server;

/// <summary>
/// Configuration for MQTT server (publisher) background service.
/// </summary>
public class MqttServerConfiguration
{
    private static readonly IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> DefaultMapper =
        new MqttCompositeMapper(
            new MqttPathProviderMapper(new AttributeBasedPathProvider(MqttConstants.DefaultConnectorName, '/')),
            new MqttAttributeMapper(MqttConstants.DefaultConnectorName));


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
    /// Gets or sets the property mapper for property-to-topic mapping.
    /// Defaults to composite of MqttPathProviderMapper and MqttAttributeMapper, both filtered by the "mqtt" connector name.
    /// </summary>
    public IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> Mapper { get; init; } = DefaultMapper;

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
    public Func<DateTimeOffset, byte[]> SourceTimestampSerializer { get; init; } = MqttHelper.DefaultSerializeTimestamp;

    /// <summary>
    /// Gets or sets the function for deserializing timestamp bytes from MQTT user properties.
    /// Default parses Unix milliseconds from UTF8. Must be paired with a matching <see cref="SourceTimestampSerializer"/>.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, DateTimeOffset?> SourceTimestampDeserializer { get; init; } = MqttHelper.DefaultDeserializeTimestamp;

    /// <summary>
    /// Validates the configuration and throws if invalid.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when configuration is invalid.</exception>
    public void Validate()
    {
        if (Mapper is null)
        {
            throw new ArgumentException("Mapper must be specified.", nameof(Mapper));
        }

        if (ValueConverter is null)
        {
            throw new ArgumentException("ValueConverter must be specified.", nameof(ValueConverter));
        }

        if (BrokerPort is < 1 or > 65535)
        {
            throw new ArgumentException($"BrokerPort must be between 1 and 65535, got: {BrokerPort}", nameof(BrokerPort));
        }

        if (MaxPendingMessagesPerClient < 0)
        {
            throw new ArgumentException($"MaxPendingMessagesPerClient must be non-negative, got: {MaxPendingMessagesPerClient}", nameof(MaxPendingMessagesPerClient));
        }

        if (BufferTime < TimeSpan.Zero)
        {
            throw new ArgumentException($"BufferTime must be non-negative, got: {BufferTime}", nameof(BufferTime));
        }

        if (InitialStateDelay < TimeSpan.Zero)
        {
            throw new ArgumentException($"InitialStateDelay must be non-negative, got: {InitialStateDelay}", nameof(InitialStateDelay));
        }
    }
}
