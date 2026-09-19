using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents an item reference in a collection or dictionary property update.
/// For collections: array order defines collection ordering, only id is required.
/// For dictionaries: id + key identifies the entry.
/// </summary>
public readonly struct SubjectPropertyItemUpdate
{
    /// <summary>
    /// The stable subject ID for this item.
    /// References a subject in the Subjects dictionary.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// The dictionary key (only for Dictionary kind properties).
    /// Null for Collection kind items where ordering comes from array position.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("key")]
    public string? Key { get; init; }
}
