namespace Namotion.Interceptor.Sources;

public class SubjectPropertyCollectionUpdate
{
    public required object Index { get; init; }

    public SubjectUpdate? Item { get; init; }
}