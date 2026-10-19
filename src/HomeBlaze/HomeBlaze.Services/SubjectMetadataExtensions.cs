using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services;

/// <summary>
/// Reads the metadata that the registry holds under <see cref="KnownAttributes.Metadata"/>: in a dynamic property of a
/// subject, and in an attribute of a property. Read its keys with <see cref="MetadataDictionaryExtensions"/>.
/// </summary>
public static class SubjectMetadataExtensions
{
    // Guards against a cycle of parents, which a walk up would follow forever.
    private const int MaxDepth = 256;

    /// <summary>Gets the metadata of the subject; null without any, and for a subject that is not registered.</summary>
    public static ImmutableDictionary<string, JsonElement>? GetMetadata(this IInterceptorSubject subject)
        => subject.TryGetRegisteredSubject()?.TryGetProperty(KnownAttributes.Metadata)?.GetValue() as ImmutableDictionary<string, JsonElement>;

    /// <summary>Gets the metadata of the property; null without any.</summary>
    public static ImmutableDictionary<string, JsonElement>? GetMetadata(this RegisteredSubjectProperty property)
        => property.TryGetAttribute(KnownAttributes.Metadata)?.GetValue() as ImmutableDictionary<string, JsonElement>;

    /// <summary>Gets whether the property holds metadata: that of its subject, or as an attribute that of a property.</summary>
    public static bool IsMetadata(this RegisteredSubjectProperty property)
        => property.IsAttribute
            ? property.AttributeMetadata.AttributeName == KnownAttributes.Metadata
            : property.Name == KnownAttributes.Metadata;

    /// <summary>
    /// Gets the store that keeps the metadata of the subject: the nearest <see cref="IMetadataStore"/> up the first
    /// parent of each subject, as the registry writes the path of the subject, else the subject itself when it is a
    /// store, such as the root storage; null without one. A storage that is held by another one has its metadata in
    /// that one.
    /// </summary>
    public static IMetadataStore? FindMetadataStore(this IInterceptorSubject subject)
    {
        var current = subject.TryGetRegisteredSubject();
        for (var depth = 0; depth < MaxDepth && current is { Parents: [var parent, ..] }; depth++)
        {
            current = parent.Property.Parent;
            if (current.Subject is IMetadataStore store)
            {
                return store;
            }
        }

        return subject as IMetadataStore;
    }
}
