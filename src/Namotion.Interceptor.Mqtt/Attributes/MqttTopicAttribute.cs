using System;
using MQTTnet.Protocol;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Mqtt.Attributes;

/// <summary>
/// Maps a property to an MQTT topic with optional per-topic QoS and Retain overrides.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
public class MqttTopicAttribute : PathAttribute
{
    /// <summary>
    /// Creates an MQTT topic mapping for a property.
    /// </summary>
    /// <param name="topic">The MQTT topic to map this property to.</param>
    /// <param name="connectorName">
    /// The connector name to associate this mapping with.
    /// Defaults to <see cref="MqttConstants.DefaultConnectorName"/> ("mqtt").
    /// </param>
    public MqttTopicAttribute(string topic, string? connectorName = null)
        : base(connectorName ?? MqttConstants.DefaultConnectorName, topic)
    {
    }

    /// <summary>
    /// Gets the MQTT topic (alias for <see cref="PathAttribute.Path"/>).
    /// </summary>
    public string Topic => Path;

    /// <summary>
    /// Gets or sets the Quality of Service level for this topic.
    /// Default is -1 (not set), which uses the configuration default.
    /// </summary>
    public MqttQualityOfServiceLevel QualityOfService { get; set; } = (MqttQualityOfServiceLevel)(-1);

    /// <summary>
    /// Gets or sets whether messages on this topic should be retained by the broker.
    /// Must be used together with <see cref="RetainSet"/> to distinguish "not set" from "false".
    /// </summary>
    public bool Retain { get; set; }

    /// <summary>
    /// Gets or sets whether <see cref="Retain"/> has been explicitly set.
    /// Required because C# attributes do not support nullable bool.
    /// </summary>
    public bool RetainSet { get; set; }

    /// <summary>
    /// Converts this attribute to an <see cref="MqttPropertyMapping"/>.
    /// </summary>
    public MqttPropertyMapping ToMapping() => new(
        Topic: Topic,
        QualityOfService: (int)QualityOfService == -1 ? null : QualityOfService,
        Retain: RetainSet ? Retain : null);
}
