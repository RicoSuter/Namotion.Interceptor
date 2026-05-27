using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mqtt.Mapping;

public class MqttFluentPropertyMapper<TSubject>
    : IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>
{
    private readonly ConcurrentDictionary<string, MqttPropertyMapping> _mappings = new();

    public MqttFluentPropertyMapper<TSubject> Map<TValue>(
        Expression<Func<TSubject, TValue>> selector,
        Action<MqttFluentMappingBuilder> configure)
    {
        var builder = new MqttFluentMappingBuilder();
        configure(builder);
        _mappings[PropertyPathHelper.GetPathFromExpression(selector.Body)] = builder.Build();
        return this;
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        [NotNullWhen(true)] out MqttPropertyMapping? mapping)
    {
        var path = PropertyPathHelper.GetPathFromProperty(property);
        if (_mappings.TryGetValue(path, out var stored))
        {
            mapping = stored;
            return true;
        }
        mapping = null;
        return false;
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
