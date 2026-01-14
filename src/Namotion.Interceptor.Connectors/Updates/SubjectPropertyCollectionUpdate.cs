using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a sparse property update for a collection item at a specific index/key.
/// </summary>
public readonly struct SubjectPropertyCollectionUpdate
{
    /// <summary>
    /// The target index (int for arrays) or key (string for dictionaries).
    /// This is the FINAL index after structural operations are applied.
    /// </summary>
    [JsonPropertyName("index")]
    public required object Index { get; init; }

    /// <summary>
    /// The subject ID for this collection item.
    /// References a subject in the Subjects dictionary.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}
