using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Mqtt.Attributes;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Layers MQTT metadata (QoS, Retain) from <see cref="MqttTopicAttribute"/> annotations onto the
/// mapping. The topic itself is a relative path segment resolved by the path-provider mapper, so this
/// mapper neither sets the topic on forward mapping nor performs reverse lookup.
/// <para>
/// Because of that, this mapper must be combined with an <see cref="MqttPathProviderMapper"/> (as the
/// default composite does). On its own it contributes only QoS and Retain and resolves no topics. This
/// differs from the OPC UA attribute mapper, which is self-sufficient: OPC UA browses hierarchically and
/// matches each node against a single level, while MQTT resolves a flat topic that requires full-path
/// composition by the path provider.
/// </para>
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
        // ToMapping() contributes QoS/Retain only; the topic is the path segment resolved by the path-provider mapper.
        var topicAttribute = FindTopicAttribute(property.ReflectionAttributes);
        mapping = topicAttribute?.ToMapping();
        return mapping is not null;
    }

    // Indexed rather than enumerated: the MQTT server's change filter runs this for every change of a property
    // without a topic, and an interface enumerator would allocate per call.
    private MqttTopicAttribute? FindTopicAttribute(IReadOnlyCollection<Attribute> attributes)
    {
        if (attributes is IList<Attribute> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is MqttTopicAttribute mqttTopic && mqttTopic.Name == _connectorName)
                {
                    return mqttTopic;
                }
            }

            return null;
        }

        foreach (var attribute in attributes)
        {
            if (attribute is MqttTopicAttribute mqttTopic && mqttTopic.Name == _connectorName)
            {
                return mqttTopic;
            }
        }

        return null;
    }

    public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        MqttLookupKey key, RegisteredSubject subject, CancellationToken cancellationToken)
    {
        // Reverse lookup (topic -> property) is owned by the path-provider mapper, which resolves the
        // composed hierarchical topic. This mapper only contributes forward QoS/Retain metadata.
        return new ValueTask<RegisteredSubjectProperty?>(result: null);
    }
}
