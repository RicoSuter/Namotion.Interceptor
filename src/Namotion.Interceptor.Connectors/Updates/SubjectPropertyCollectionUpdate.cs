namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a property update for a collection item at a specific index/key.
/// Index refers to the FINAL position after structural Operations are applied.
/// </summary>
public class SubjectPropertyCollectionUpdate
{
    /// <summary>
    /// The target index (int for arrays) or key (object for dictionaries).
    /// This is the FINAL index after structural operations are applied.
    /// </summary>
    public required object Index { get; init; }

    /// <summary>
    /// The property updates for the item at this index.
    /// </summary>
    public SubjectUpdate? Item { get; init; }
}
