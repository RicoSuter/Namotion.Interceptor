using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a structural operation on a collection.
/// </summary>
public class SubjectCollectionOperation
{
    /// <summary>
    /// The type of operation.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonPropertyName("action")]
    public SubjectCollectionOperationType Action { get; init; }

    /// <summary>
    /// Target index (int for arrays) or key (string for dictionaries).
    /// </summary>
    [JsonPropertyName("index")]
    public required object Index { get; init; }

    /// <summary>
    /// Source index for Move operations (arrays only), numbered against the collection
    /// after all Remove and Insert operations of the same update have been applied.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("fromIndex")]
    public int? FromIndex { get; init; }

    /// <summary>
    /// The subject ID for Insert operations.
    /// References a subject in the Subjects dictionary.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}
