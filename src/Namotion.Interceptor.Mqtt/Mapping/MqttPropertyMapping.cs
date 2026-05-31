using MQTTnet.Protocol;
using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// MQTT-specific property mapping carrying topic, QoS, and retain settings.
/// </summary>
public sealed record MqttPropertyMapping(
    string? Topic = null,
    MqttQualityOfServiceLevel? QualityOfService = null,
    bool? Retain = null)
    : IPropertyMapping<MqttPropertyMapping>
{
    public static MqttPropertyMapping Merge(MqttPropertyMapping primary, MqttPropertyMapping fallback) => new(
        Topic: primary.Topic ?? fallback.Topic,
        QualityOfService: primary.QualityOfService ?? fallback.QualityOfService,
        Retain: primary.Retain ?? fallback.Retain);
}
