using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mqtt.Mapping;

public class MqttPathProviderMapper
    : IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>
{
    private readonly PathProviderBase _pathProvider;

    public MqttPathProviderMapper(PathProviderBase pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        [NotNullWhen(true)] out MqttPropertyMapping? mapping)
    {
        if (!_pathProvider.IsPropertyIncluded(property))
        {
            mapping = null;
            return false;
        }

        var topic = property.TryGetPath(_pathProvider, rootSubject: null);
        if (topic is null)
        {
            mapping = null;
            return false;
        }

        mapping = new MqttPropertyMapping(Topic: topic);
        return true;
    }

    public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject root, MqttLookupKey key, CancellationToken cancellationToken)
    {
        var (result, _) = root.Subject.TryGetPropertyFromPath(key.Topic, _pathProvider);
        return new ValueTask<RegisteredSubjectProperty?>(result);
    }
}
