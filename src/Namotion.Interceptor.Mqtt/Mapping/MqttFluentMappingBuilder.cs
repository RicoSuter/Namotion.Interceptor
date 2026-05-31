using MQTTnet.Protocol;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Fluent builder for constructing <see cref="MqttPropertyMapping"/> instances.
/// </summary>
public sealed class MqttFluentMappingBuilder
{
    private string? _topic;
    private MqttQualityOfServiceLevel? _qos;
    private bool? _retain;

    public MqttFluentMappingBuilder WithTopic(string topic) { _topic = topic; return this; }
    public MqttFluentMappingBuilder WithQualityOfService(MqttQualityOfServiceLevel qos) { _qos = qos; return this; }
    public MqttFluentMappingBuilder WithRetain(bool retain) { _retain = retain; return this; }

    internal MqttPropertyMapping Build() => new(_topic, _qos, _retain);
}
