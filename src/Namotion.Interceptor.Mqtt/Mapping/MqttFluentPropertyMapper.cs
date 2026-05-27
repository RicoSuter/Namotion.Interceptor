using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Fluent property mapper for configuring MQTT topic mappings using lambda expressions.
/// </summary>
/// <typeparam name="TSubject">The subject type whose properties are being mapped.</typeparam>
public class MqttFluentPropertyMapper<TSubject>
    : FluentPropertyMapperBase<TSubject, MqttPropertyMapping>,
      IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>
{
    public MqttFluentPropertyMapper<TSubject> Map<TValue>(
        Expression<Func<TSubject, TValue>> selector,
        Action<MqttFluentMappingBuilder> configure)
    {
        var builder = new MqttFluentMappingBuilder();
        configure(builder);
        SetMapping(selector, builder.Build());
        return this;
    }

    public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject root, MqttLookupKey key, CancellationToken cancellationToken)
    {
        foreach (var property in root.Properties)
        {
            if (property.IsAttribute)
                continue;

            if (TryGetMapping(property, out var mapping) && mapping.Topic == key.Topic)
                return new ValueTask<RegisteredSubjectProperty?>(property);
        }
        return new ValueTask<RegisteredSubjectProperty?>(result: null);
    }
}
