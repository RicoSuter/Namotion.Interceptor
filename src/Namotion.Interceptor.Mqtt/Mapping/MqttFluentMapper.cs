using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Code-based fluent mapper for configuring MQTT property mappings at runtime.
/// </summary>
public class MqttFluentMapper<TSubject>
    : IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>
{
    private readonly ConcurrentDictionary<string, MqttPropertyMapping> _mappings = new();

    public MqttFluentMapper<TSubject> Map<TValue>(
        Expression<Func<TSubject, TValue>> selector,
        Action<MqttFluentMappingBuilder> configure)
    {
        var builder = new MqttFluentMappingBuilder();
        configure(builder);
        _mappings[ExpressionPathHelper.GetPathFromExpression(selector.Body)] = builder.Build();
        return this;
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out MqttPropertyMapping? mapping)
    {
        var path = property.GetPath();
        if (_mappings.TryGetValue(path, out var stored))
        {
            mapping = stored;
            return true;
        }
        mapping = null;
        return false;
    }

    public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        MqttLookupKey key, RegisteredSubject rootSubject, CancellationToken cancellationToken)
    {
        foreach (var property in rootSubject.GetAllProperties())
        {
            if (property.IsAttribute)
                continue;

            if (TryGetMapping(property, rootSubject.Subject, out var mapping) && mapping.Topic == key.Topic)
                return new ValueTask<RegisteredSubjectProperty?>(property);
        }
        return new ValueTask<RegisteredSubjectProperty?>(result: null);
    }
}
