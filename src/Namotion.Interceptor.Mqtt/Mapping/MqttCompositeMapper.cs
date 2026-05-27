using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mqtt.Mapping;

/// <summary>
/// Combines multiple MQTT property mappers with last-wins merge semantics.
/// </summary>
public sealed class MqttCompositeMapper
    : IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>
{
    private readonly CompositeMapper<MqttPropertyMapping> _forward;
    private readonly IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>[] _mappers;

    public MqttCompositeMapper(
        params IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>[] mappers)
    {
        _mappers = mappers;
        _forward = new CompositeMapper<MqttPropertyMapping>(
            mappers.Cast<IPropertyMapper<MqttPropertyMapping>>().ToArray());
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out MqttPropertyMapping? mapping)
        => _forward.TryGetMapping(property, rootSubject, out mapping);

    public async ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        MqttLookupKey key, RegisteredSubject rootSubject, CancellationToken cancellationToken)
    {
        for (var i = _mappers.Length - 1; i >= 0; i--)
        {
            var found = await _mappers[i].TryGetPropertyAsync(key, rootSubject, cancellationToken)
                .ConfigureAwait(false);
            if (found is not null) return found;
        }
        return null;
    }
}
