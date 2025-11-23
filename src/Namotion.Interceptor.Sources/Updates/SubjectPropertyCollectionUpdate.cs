namespace Namotion.Interceptor.Sources.Updates;

public class SubjectPropertyCollectionUpdate
{
    /// <summary>
    /// Gets the index of the collection item.
    /// </summary>
    public required object Index { get; init; }

    /// <summary>
    /// Gets the collection item.
    /// </summary>
    public SubjectUpdate? Item { get; init; }
}
