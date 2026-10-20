using System.Text.Json.Serialization;

namespace HomeBlaze.Services;

/// <summary>A subject, or a property of it, whose metadata <see cref="MetadataSearch"/> found.</summary>
/// <param name="Path">The path of the subject relative to the searched root, empty for the root itself.</param>
/// <param name="Property">The name of the property, or null for the subject.</param>
public sealed record MetadataSearchMatch(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("property"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Property);
