using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Covers;

/// <summary>
/// Controller interface for devices with adjustable position.
/// </summary>
[SubjectAbstraction]
[Description("Controls position of a cover device.")]
public interface IPositionController
{
    /// <summary>
    /// Sets the position.
    /// </summary>
    /// <param name="position">Position (0 = fully open, 1 = fully closed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Operation]
    Task SetPositionAsync(
        [OperationParameter(Unit = StateUnit.Percent)] decimal position,
        CancellationToken cancellationToken);
}
