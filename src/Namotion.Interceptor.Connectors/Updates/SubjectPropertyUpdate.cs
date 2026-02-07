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
    /// The subject ID for Object kind properties.
    /// Null means the object reference is null.
    /// Omitted entirely when null (no "id": null in JSON).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Structural operations (Remove, Insert, Move) for Collection/Dictionary kinds.
    /// Applied in order BEFORE sparse property updates.
    /// Note: Move is only valid for Collection kind.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("operations")]
    public List<SubjectCollectionOperation>? Operations { get; set; }

    /// <summary>
    /// Sparse property updates by final index/key for Collection/Dictionary kinds.
    /// Applied AFTER structural operations.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("items")]
    public List<SubjectPropertyItemUpdate>? Items { get; set; }

    /// <summary>
    /// Total count of collection/dictionary after all operations.
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
