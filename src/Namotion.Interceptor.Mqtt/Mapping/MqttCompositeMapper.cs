using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Combines multiple MQTT mappers: forward composition merges partial mappings ("last wins"),
/// reverse lookup tries mappers in reverse order and returns the first match.
/// </summary>
public sealed class MqttCompositeMapper(
    params IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>[] mappers)
    : ReverseCompositeMapper<MqttPropertyMapping, MqttLookupKey>(mappers);
