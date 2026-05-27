using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Extends <see cref="IPropertyMapper{TMapping}"/> with reverse lookup from external keys back to properties.
/// </summary>
public interface IReversePropertyMapper<TMapping, in TKey> : IPropertyMapper<TMapping>
{
    /// <summary>
    /// Attempts to resolve a registered property from an external key.
    /// </summary>
    ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        TKey key,
        RegisteredSubject root,
        CancellationToken cancellationToken);
}
