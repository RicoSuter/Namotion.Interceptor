namespace Namotion.Interceptor.Connectors.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolFloodCollection
{
    private ThreadPoolFloodCollection()
    {
    }

    public const string Name = nameof(ThreadPoolFloodCollection);
}
