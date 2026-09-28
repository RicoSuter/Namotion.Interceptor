namespace Namotion.Interceptor.Tracking.Tests.Change;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DerivedPropertyWriteGenerationCollection
{
    private DerivedPropertyWriteGenerationCollection()
    {
    }

    public const string Name = nameof(DerivedPropertyWriteGenerationCollection);
}
