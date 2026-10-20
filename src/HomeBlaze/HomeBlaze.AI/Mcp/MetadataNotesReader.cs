using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Reads the notes of the metadata of a subject and of its properties, which search_markdown searches with the pages.
/// </summary>
internal static class MetadataNotesReader
{
    /// <summary>
    /// Gets the notes of the subject, with its path and title, and those of each of its properties that the path provider
    /// includes, with the path of the subject followed by the property name and the display name of the property.
    /// </summary>
    public static IEnumerable<(string Path, string? Title, string Notes)> GetNotes(
        RegisteredSubject subject, string path, PathProviderBase pathProvider)
    {
        if (subject.Subject.GetMetadata().GetString(MetadataKeys.Notes) is { } notes)
        {
            yield return (path, (subject.Subject as ITitleProvider)?.Title, notes);
        }

        // The metadata of a property is its attribute, found in one pass over the properties.
        foreach (var attribute in subject.Properties)
        {
            if (!attribute.IsAttribute || attribute.AttributeMetadata.AttributeName != KnownAttributes.Metadata)
            {
                continue;
            }

            var propertyNotes = (attribute.GetValue() as ImmutableDictionary<string, JsonElement>).GetString(MetadataKeys.Notes);
            if (propertyNotes is not null &&
                subject.TryGetProperty(attribute.AttributeMetadata.PropertyName) is { } property &&
                pathProvider.IsPropertyIncluded(property))
            {
                var propertyPath = path.EndsWith('/') ? path + property.Name : $"{path}/{property.Name}";
                yield return (propertyPath, property.GetDisplayName(), propertyNotes);
            }
        }
    }
}
