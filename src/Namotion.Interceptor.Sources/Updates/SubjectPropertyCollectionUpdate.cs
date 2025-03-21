namespace Namotion.Interceptor.Sources.Updates;

public class SubjectPropertyCollectionUpdate
{
    public required object Index { get; init; }

    public SubjectUpdate? Item { get; init; }
}