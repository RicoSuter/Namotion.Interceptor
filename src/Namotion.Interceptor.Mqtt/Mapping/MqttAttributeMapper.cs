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
        // Single-pass lookup to avoid LINQ allocation. ToMapping() contributes QoS/Retain only;
        // the topic is the path segment resolved by the path-provider mapper.
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
        // Reverse lookup (topic -> property) is owned by the path-provider mapper, which resolves the
        // composed hierarchical topic. This mapper only contributes forward QoS/Retain metadata.
        return new ValueTask<RegisteredSubjectProperty?>(result: null);
    }
}
