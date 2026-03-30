using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Mcp.Models;

/// <summary>
/// Base class for subject node properties. Mirrors the RegisteredSubjectProperty model:
/// scalar properties have values, subject properties contain other subjects.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ScalarProperty), "value")]
[JsonDerivedType(typeof(SubjectObjectProperty), "object")]
[JsonDerivedType(typeof(SubjectCollectionProperty), "collection")]
[JsonDerivedType(typeof(SubjectDictionaryProperty), "dictionary")]
public abstract record SubjectNodeProperty(
    [property: JsonPropertyName("name")] string Name);

/// <summary>
/// A scalar property with a value (string, number, boolean, etc.).
/// </summary>
public record ScalarProperty(
    string Name,
    [property: JsonPropertyName("value")] object? Value,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("isWritable")] bool IsWritable = false,
    [property: JsonPropertyName("attributes")] List<PropertyAttribute>? Attributes = null
) : SubjectNodeProperty(Name);

/// <summary>
/// A property referencing a single subject.
/// </summary>
public record SubjectObjectProperty(
    string Name,
    [property: JsonPropertyName("child")] SubjectNode? Child,
    [property: JsonPropertyName("isCollapsed")] bool IsCollapsed = false
) : SubjectNodeProperty(Name);

/// <summary>
/// A property containing an ordered collection of subjects.
/// </summary>
public record SubjectCollectionProperty(
    string Name,
    [property: JsonPropertyName("children")] List<SubjectNode>? Children = null,
    [property: JsonPropertyName("count")] int? Count = null,
    [property: JsonPropertyName("itemType")] string? ItemType = null,
    [property: JsonPropertyName("isCollapsed")] bool IsCollapsed = false
) : SubjectNodeProperty(Name);

/// <summary>
/// A property containing a keyed dictionary of subjects.
/// </summary>
public record SubjectDictionaryProperty(
    string Name,
    [property: JsonPropertyName("children")] Dictionary<string, SubjectNode>? Children = null,
    [property: JsonPropertyName("count")] int? Count = null,
    [property: JsonPropertyName("itemType")] string? ItemType = null,
    [property: JsonPropertyName("isCollapsed")] bool IsCollapsed = false
) : SubjectNodeProperty(Name);

/// <summary>
/// An attribute attached to a scalar property.
/// </summary>
public record PropertyAttribute(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] object? Value);
