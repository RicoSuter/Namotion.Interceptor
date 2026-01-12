using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a property update within a subject.
/// </summary>
public class SubjectPropertyUpdate
{
    /// <summary>
    /// The kind of property update.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonPropertyName("kind")]
    public SubjectPropertyUpdateKind Kind { get; set; }

    /// <summary>
    /// The value for Value kind properties.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("value")]
    public object? Value { get; set; }

    /// <summary>
    /// The timestamp of when the value was changed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>
    /// The subject ID for Item kind properties.
    /// Null means the item reference is null.
    /// Omitted entirely when null (no "id": null in JSON).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Structural operations (Remove, Insert, Move) for Collection kind.
    /// Applied in order BEFORE sparse property updates.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("operations")]
    public List<SubjectCollectionOperation>? Operations { get; set; }

    /// <summary>
    /// Sparse property updates by final index/key for Collection kind.
    /// Applied AFTER structural operations.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("collection")]
    public List<SubjectPropertyCollectionUpdate>? Collection { get; set; }

    /// <summary>
    /// Total count of collection after all operations.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    /// <summary>
    /// Attribute updates for this property.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("attributes")]
    public Dictionary<string, SubjectPropertyUpdate>? Attributes { get; set; }

    /// <summary>
    /// Extension data for custom properties added by processors.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? ExtensionData { get; set; }
}
