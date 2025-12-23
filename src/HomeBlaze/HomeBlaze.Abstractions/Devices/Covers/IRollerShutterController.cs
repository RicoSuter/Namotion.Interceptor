using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Covers;

/// <summary>
/// Controller interface for roller shutters.
/// </summary>
public interface IRollerShutterController : IPositionController
{
    /// <summary>
    /// Opens the shutter completely.
    /// </summary>
    [Operation]
    Task OpenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Closes the shutter completely.
    /// </summary>
    [Operation]
    Task CloseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops the shutter at its current position.
    /// </summary>
    [Operation]
    Task StopAsync(CancellationToken cancellationToken);
}
