using HomeBlaze.Installation;
using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// A root that holds a game, and optionally an installation, and counts the configurations written through it, as a storage does.
/// </summary>
[InterceptorSubject]
public partial class TestRoot : IConfigurationWriter
{
    private int _writeCount;

    public partial Game? Game { get; set; }

    public partial HomeBlazeInstallation? Installation { get; set; }

    public bool FailsToWrite { get; set; }

    /// <summary>
    /// A task that a write waits for before it counts, so a test can hold a save while it runs.
    /// </summary>
    public Task? WriteBlocker { get; set; }

    public int WriteCount => Volatile.Read(ref _writeCount);

    public TestRoot()
    {
        Game = null;
        Installation = null;
    }

    public async Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (WriteBlocker is { } blocker)
        {
            await blocker.WaitAsync(cancellationToken);
        }

        Interlocked.Increment(ref _writeCount);
        return !FailsToWrite;
    }
}
