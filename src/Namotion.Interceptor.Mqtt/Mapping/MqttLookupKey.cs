namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Key for reverse-looking up a property from an MQTT topic.
/// </summary>
public readonly record struct MqttLookupKey(string Topic);
