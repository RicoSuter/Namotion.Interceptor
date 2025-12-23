using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// Controller interface for devices with adjustable volume.
/// </summary>
public interface IVolumeController
{
    /// <summary>
    /// Sets the volume level.
    /// </summary>
    /// <param name="volume">Volume level (0-100%).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Operation]
    Task SetVolumeAsync(
        [OperationParameter(Unit = StateUnit.Percent)] decimal volume,
        CancellationToken cancellationToken);
}
