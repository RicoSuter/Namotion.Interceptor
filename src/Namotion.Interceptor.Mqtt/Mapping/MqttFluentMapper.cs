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
        var path = property.TryGetPath(rootSubject: rootSubject);
        if (path is not null && _mappings.TryGetValue(path, out var stored))
        {
            mapping = stored;
            return true;
        }
        mapping = null;
        return false;
    }

    public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        MqttLookupKey key, RegisteredSubject subject, CancellationToken cancellationToken)
    {
        // MQTT is a flat connector: per the IReversePropertyMapper contract, callers pass the connected
        // root subject here (not a per-level subject). The stored keys are full paths from the root, so
        // resolving each property's path relative to subject.Subject (== the root) yields matching keys.
        // Unlike OPC UA, there is no hierarchical browse, so no separate root needs to be threaded in.
        //
        // TODO(perf): this is an O(n) scan over all properties (with a HashSet allocation in
        // GetAllProperties and a GetPath walk per property) on every reverse lookup. Callers cache the
        // result per topic, so steady state is fine, but a topic -> property index built from _mappings
        // would remove the first-hit cost if profiling shows it matters.
        foreach (var property in subject.GetAllProperties())
        {
            if (property.IsAttribute)
                continue;

            if (TryGetMapping(property, subject.Subject, out var mapping) && mapping.Topic == key.Topic)
                return new ValueTask<RegisteredSubjectProperty?>(property);
        }
        return new ValueTask<RegisteredSubjectProperty?>(result: null);
    }
}
