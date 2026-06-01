using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Code-based MQTT mapper produced by <see cref="MqttFluentMapperBuilder{TRoot}.Build"/>. Combines a
/// <see cref="FluentPathProvider{TMetadata}"/>-backed topic mapper with a metadata mapper (QoS and retain).
/// Compose it into an <see cref="MqttCompositeMapper"/>, typically after the attribute mappers so fluent wins.
/// </summary>
public sealed class MqttFluentMapper : ReverseCompositeMapper<MqttPropertyMapping, MqttLookupKey>
{
    public MqttFluentMapper(FluentMappingRegistry<MqttPropertyMapping> registry, char pathSeparator = '/')
        : base(
            new MqttPathProviderMapper(new FluentPathProvider<MqttPropertyMapping>(registry, pathSeparator)),
            new FluentMetadataMapper<MqttPropertyMapping, MqttLookupKey>(registry))
    {
    }
}
