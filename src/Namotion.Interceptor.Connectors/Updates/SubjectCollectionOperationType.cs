namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Types of structural operations on collections and dictionaries.
/// </summary>
public enum SubjectCollectionOperationType
{
    /// <summary>
    /// Remove the item identified by stable subject ID (or key for dictionaries).
    /// </summary>
    Remove = 0,

    /// <summary>
    /// Insert a new item identified by stable subject ID at the position after AfterId (or at head when AfterId is null).
    /// For dictionaries, the Key field identifies the dictionary entry.
    /// Idempotent for collections: skipped if an item with the same ID already exists.
    /// </summary>
    Insert = 1,

    /// <summary>
    /// Move the item identified by stable subject ID to the position after AfterId (or to head when AfterId is null).
    /// Collections only. Identity preserved.
    /// </summary>
    Move = 2
}
