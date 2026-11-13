namespace Namotion.Interceptor.Tracking.Tests.Change;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolFloodCollection
{
    private ThreadPoolFloodCollection()
    {
    }

    public const string Name = nameof(ThreadPoolFloodCollection);
}
