namespace Connectors.Server.Grinder;

/// <summary>
/// The API of a grinder that is a separate device and owns its grind size.
/// </summary>
public interface IGrinderDevice
{
    /// <summary>Connects to the device; disposing the returned connection disconnects.</summary>
    Task<IAsyncDisposable> ConnectAsync(CancellationToken cancellationToken);

    Task<int> ReadGrindSizeAsync(CancellationToken cancellationToken);

    Task WriteGrindSizeAsync(int grindSize, CancellationToken cancellationToken);

    /// <summary>Yields the grind size whenever someone turns the knob on the device.</summary>
    IAsyncEnumerable<int> WatchGrindSizeAsync(CancellationToken cancellationToken);
}
