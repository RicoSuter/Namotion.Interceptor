namespace Namotion.Interceptor.Tracking.Tests.Change;

// Tests that flood the thread pool's global queue occupy every pool thread while they run.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolFloodCollection
{
    private ThreadPoolFloodCollection()
    {
    }

    public const string Name = "ThreadPoolFlood";
}
