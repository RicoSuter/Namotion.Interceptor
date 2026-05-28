using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Mqtt.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Maps properties to MQTT topics using <see cref="MqttTopicAttribute"/> annotations.
/// Supports both forward mapping (property to topic) and reverse lookup (topic to property).
/// </summary>
public class MqttAttributeMapper : IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>
{
    private readonly string _connectorName;

    public MqttAttributeMapper(string? connectorName = null)
    {
        _connectorName = connectorName ?? MqttConstants.DefaultConnectorName;
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out MqttPropertyMapping? mapping)
    {
        // Single-pass lookup to avoid LINQ allocation
        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is MqttTopicAttribute mqttTopic && mqttTopic.Name == _connectorName)
            {
                mapping = mqttTopic.ToMapping();
                return true;
            }
        }
        mapping = null;
        return false;
    }

    public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        MqttLookupKey key, RegisteredSubject subject, CancellationToken cancellationToken)
    {
        var topic = key.Topic;
        foreach (var property in subject.GetAllProperties())
        {
            foreach (var attribute in property.ReflectionAttributes)
            {
                if (attribute is MqttTopicAttribute mqttTopic &&
                    mqttTopic.Name == _connectorName &&
                    mqttTopic.Topic == topic)
                {
                    return new ValueTask<RegisteredSubjectProperty?>(property);
                }
            }
        }
        return new ValueTask<RegisteredSubjectProperty?>((RegisteredSubjectProperty?)null);
    }
}
