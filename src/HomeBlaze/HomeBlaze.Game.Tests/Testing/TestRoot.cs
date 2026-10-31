using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// A root that holds a game and counts the configurations written through it, as a storage does.
/// </summary>
[InterceptorSubject]
public partial class TestRoot : IConfigurationWriter
{
    private int _writeCount;

    public partial Game? Game { get; set; }

    public bool FailsToWrite { get; set; }

    public int WriteCount => Volatile.Read(ref _writeCount);

    public TestRoot()
    {
        Game = null;
    }

    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _writeCount);
        return Task.FromResult(!FailsToWrite);
    }
}
