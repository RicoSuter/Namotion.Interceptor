using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

public interface IReversePropertyMapper<TMapping, in TKey> : IPropertyMapper<TMapping>
{
    ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject root,
        TKey key,
        CancellationToken cancellationToken);
}
