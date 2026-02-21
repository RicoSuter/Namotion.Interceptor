using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a structural operation on a collection or dictionary.
/// All operations reference subjects by stable ID.
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
    /// The stable ID of the target subject (required for all operations).
    /// Also the key in the Subjects dictionary for Insert operations.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// For Insert and Move: the stable ID of the predecessor item.
    /// Null means place at head of collection.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("afterId")]
    public string? AfterId { get; init; }

    /// <summary>
    /// Optional key for dictionary operations (Insert/Remove).
    /// Not used for collection operations.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("key")]
    public string? Key { get; init; }
}
