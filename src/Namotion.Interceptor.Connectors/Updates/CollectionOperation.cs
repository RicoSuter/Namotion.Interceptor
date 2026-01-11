using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a structural operation on a collection (not property updates).
/// </summary>
public class CollectionOperation
{
    /// <summary>
    /// The type of structural operation.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required CollectionOperationType Action { get; init; }

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

/// <summary>
/// Types of structural operations on collections.
/// </summary>
public enum CollectionOperationType
{
    /// <summary>
    /// Remove item at index/key. For arrays, subsequent items shift down.
    /// </summary>
    Remove = 0,

    /// <summary>
    /// Insert new item at index/key. For arrays, subsequent items shift up.
    /// </summary>
    Insert = 1,

    /// <summary>
    /// Move item from FromIndex to Index. Arrays only. Identity preserved.
    /// </summary>
    Move = 2
}
