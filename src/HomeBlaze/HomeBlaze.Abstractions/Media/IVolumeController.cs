using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// Controller interface for devices with adjustable volume.
/// </summary>
[SubjectAbstraction]
[Description("Controls volume level.")]
public interface IVolumeController
{
    /// <summary>
    /// Sets the volume level.
    /// </summary>
    /// <param name="volume">Volume level (0..1).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Operation]
    Task SetVolumeAsync(
        [OperationParameter(Unit = StateUnit.Percent)] decimal volume,
        CancellationToken cancellationToken);
}
