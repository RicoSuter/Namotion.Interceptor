namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Types of structural operations on collections.
/// </summary>
public enum SubjectCollectionOperationType
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