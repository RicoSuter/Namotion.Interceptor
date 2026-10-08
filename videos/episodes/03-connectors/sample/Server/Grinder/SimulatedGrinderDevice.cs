using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Connectors.Server.Grinder;

/// <summary>
/// In-process stand-in for the grinder device, so the sample runs without hardware.
/// </summary>
public sealed class SimulatedGrinderDevice : IGrinderDevice
{
    private readonly Channel<int> _knob = Channel.CreateUnbounded<int>();
    private int _grindSize = 6;

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<int> ReadGrindSizeAsync(CancellationToken cancellationToken) => Task.FromResult(Volatile.Read(ref _grindSize));

    public Task WriteGrindSizeAsync(int grindSize, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _grindSize, grindSize);
        return Task.CompletedTask;
    }

    /// <summary>Simulates someone turning the knob on the device.</summary>
    public void TurnKnob(int grindSize)
    {
        Volatile.Write(ref _grindSize, grindSize);
        _knob.Writer.TryWrite(grindSize);
    }

    public async IAsyncEnumerable<int> WatchGrindSizeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var grindSize in _knob.Reader.ReadAllAsync(cancellationToken))
        {
            yield return grindSize;
        }
    }
}
