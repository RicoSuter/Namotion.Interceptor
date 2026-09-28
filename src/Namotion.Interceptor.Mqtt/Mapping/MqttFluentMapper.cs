using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Code-based MQTT mapper produced by <see cref="MqttFluentMapperBuilder{TRoot}.Build"/>. Resolves topics via a
/// <see cref="FluentPathProvider{TMetadata}"/> (inherited from <see cref="MqttPathProviderMapper"/>) and layers
/// the registered QoS/retain metadata on top. Compose it into an <see cref="MqttCompositeMapper"/>, typically
/// after the attribute mappers so fluent wins on conflicts. Reverse lookup is inherited from the base mapper.
/// </summary>
public sealed class MqttFluentMapper : MqttPathProviderMapper
{
    private readonly FluentMappingRegistry<MqttPropertyMapping> _registry;

    public MqttFluentMapper(FluentMappingRegistry<MqttPropertyMapping> registry, char pathSeparator = '/')
        : base(new FluentPathProvider<MqttPropertyMapping>(registry, pathSeparator))
    {
        _registry = registry;
    }

    /// <inheritdoc />
    public override bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out MqttPropertyMapping? mapping)
    {
        if (!base.TryGetMapping(property, rootSubject, out var pathMapping))
        {
            mapping = null;
            return false;
        }

        // The base mapper supplies the topic; the registry supplies QoS/retain (which win on overlap).
        mapping = _registry.TryGetPropertyMetadata(property.Subject.GetType(), property.Name, out var metadata)
            ? MqttPropertyMapping.Merge(metadata, pathMapping)
            : pathMapping;
       
        return true;
    }
}
