using System.Text.Json;
using Namotion.Interceptor;

namespace HomeBlaze.Components.Abstractions.Pages;

/// <summary>
/// Keeps the metadata of subjects and of their properties, and is found up the parent chain of a subject. The registry
/// holds the metadata of a subject in its dynamic property <c>HomeBlaze:Metadata</c>, and that of a property in its
/// attribute of that name; both change only through the store.
/// </summary>
public interface IMetadataStore
{
    /// <summary>
    /// Gets whether the store keeps the metadata of the subject and of its properties. Thread-safe.
    /// </summary>
    bool CanStoreMetadata(IInterceptorSubject subject);

    /// <summary>
    /// Applies the metadata that the store keeps for a subject that attached and for its properties, or for a property
    /// that was added to an attached subject, so that a subject or property that appears after the store applied its
    /// metadata takes it. Called for every subject that attaches below the store and every property added later, also
    /// for those whose metadata the store does not keep, which it leaves alone. Thread-safe.
    /// </summary>
    /// <param name="subject">The subject that attached, or the subject of the property.</param>
    /// <param name="propertyName">The name of the property that was added, neither the metadata nor an attribute; null for a subject that attached.</param>
    void ApplyStoredMetadata(IInterceptorSubject subject, string? propertyName);

    /// <summary>
    /// Merges values, a JSON object, into the metadata of the subject, or of its property
    /// <paramref name="propertyName"/>, at the top level: a key with a value is set, a nested value replacing the old one
    /// as a whole, a key with null is removed, and every other key stays. Keys are matched exactly. The
    /// registry holds the new metadata when the task completes.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="propertyName">The name of a property of the subject, or null for the subject itself.</param>
    /// <param name="values">A JSON object.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">
    /// The write is refused: the store does not keep the metadata of the target, the target was replaced or removed,
    /// the file cannot be read, the store is not connected, a value has the wrong shape or a limit is exceeded. The
    /// message says which.
    /// </exception>
    Task SetMetadataAsync(IInterceptorSubject subject, string? propertyName, JsonElement values, CancellationToken cancellationToken);
}
