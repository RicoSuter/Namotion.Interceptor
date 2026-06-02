using MQTTnet.Protocol;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Per-member fluent configuration for MQTT. <see cref="WithSegment"/> sets the topic level (the path
/// segment); QoS and Retain are protocol metadata.
/// </summary>
public sealed class MqttFluentPropertyBuilder
{
    private string? _segment;
    private MqttQualityOfServiceLevel? _qualityOfService;
    private bool? _retain;

    /// <summary>Sets the topic level (path segment) for this property, composed with parent segments into the full topic.</summary>
    public MqttFluentPropertyBuilder WithSegment(string segment) { _segment = segment; return this; }

    /// <summary>Sets the per-property quality of service level.</summary>
    public MqttFluentPropertyBuilder WithQualityOfService(MqttQualityOfServiceLevel qualityOfService) { _qualityOfService = qualityOfService; return this; }

    /// <summary>Sets the per-property retain flag.</summary>
    public MqttFluentPropertyBuilder WithRetain(bool retain) { _retain = retain; return this; }

    internal (string? Segment, MqttPropertyMapping Metadata) Build()
        => (_segment, new MqttPropertyMapping(QualityOfService: _qualityOfService, Retain: _retain));
}
