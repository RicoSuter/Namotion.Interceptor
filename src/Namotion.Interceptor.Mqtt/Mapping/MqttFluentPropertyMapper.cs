using System;
using System.Linq.Expressions;
using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Fluent property mapper for configuring MQTT topic mappings using lambda expressions.
/// </summary>
/// <typeparam name="TSubject">The subject type whose properties are being mapped.</typeparam>
public class MqttFluentPropertyMapper<TSubject> : FluentPropertyMapperBase<TSubject, MqttPropertyMapping>
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
}
