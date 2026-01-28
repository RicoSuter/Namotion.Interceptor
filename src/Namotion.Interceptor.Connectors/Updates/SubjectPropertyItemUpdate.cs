using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a sparse property update for an item at a specific index/key.
/// </summary>
public readonly struct SubjectPropertyItemUpdate
{
    /// <summary>
    /// The target index (int for arrays) or key (string for dictionaries).
    /// This is the FINAL index after structural operations are applied.
    /// </summary>
    [JsonPropertyName("index")]
    public required object Index { get; init; }

    /// <summary>
    /// The subject ID for this item.
    /// References a subject in the Subjects dictionary.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}
