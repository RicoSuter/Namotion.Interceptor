using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a structural operation on a collection (not property updates).
/// </summary>
public class SubjectCollectionOperation
{
    /// <summary>
    /// The type of structural operation.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required SubjectCollectionOperationType Action { get; init; }

    /// <summary>
    /// Target index (int for arrays) or key (object for dictionaries).
    /// For Remove: the index/key to remove.
    /// For Insert: the index/key where to insert.
    /// For Move: the destination index.
    /// </summary>
    public required object Index { get; init; }

    /// <summary>
    /// Source index for Move action. Arrays only.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FromIndex { get; init; }

    /// <summary>
    /// The item to insert. Only for Insert action.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SubjectUpdate? Item { get; init; }
}