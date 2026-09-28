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
    /// <param name="key">The external key to resolve.</param>
    /// <param name="subject">
    /// The subject whose subtree to search. Connectors that browse hierarchically pass the current
    /// subject (one level at a time); flat connectors pass the graph root.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        TKey key,
        RegisteredSubject subject,
        CancellationToken cancellationToken);
}
