using System;
using MQTTnet.Protocol;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Mqtt.Attributes;

/// <summary>
/// Tri-state Retain override, used because C# attributes do not support nullable bool.
/// </summary>
public enum MqttRetainMode
{
    /// <summary>Not set: uses the configuration default.</summary>
    Unset = -1,

    /// <summary>Override to false: do not retain messages on this topic.</summary>
    False = 0,

    /// <summary>Override to true: retain messages on this topic.</summary>
    True = 1
}

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
    /// <param name="pathProviderName">
    /// The path-provider name carried on the underlying <see cref="PathAttribute"/>, used to
    /// filter which path provider and attribute mapper pick this attribute up.
    /// Defaults to <see cref="MqttConstants.DefaultPathProviderName"/> ("mqtt").
    /// </param>
    public MqttTopicAttribute(string topic, string? pathProviderName = null)
        : base(pathProviderName ?? MqttConstants.DefaultPathProviderName, topic)
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
    /// Gets or sets whether messages on this topic should be retained by the broker. When set to
    /// <see cref="MqttRetainMode.True"/> or <see cref="MqttRetainMode.False"/>, overrides
    /// <c>UseRetainedMessages</c> on the client/server configuration for this topic.
    /// Default is <see cref="MqttRetainMode.Unset"/>, which uses the configuration default.
    /// </summary>
    public MqttRetainMode Retain { get; set; } = MqttRetainMode.Unset;

    /// <summary>
    /// Converts the QoS and Retain metadata of this attribute to an <see cref="MqttPropertyMapping"/>.
    /// The topic itself is a relative path segment (carried by the <see cref="PathAttribute"/> base)
    /// and is resolved hierarchically by the path-provider mapper, so it is intentionally not set here.
    /// </summary>
    public MqttPropertyMapping ToMapping() => new(
        Topic: null,
        QualityOfService: (int)QualityOfService == -1 ? null : QualityOfService,
        Retain: Retain switch
        {
            MqttRetainMode.True => true,
            MqttRetainMode.False => false,
            _ => null
        });
}
