using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Light;

/// <summary>
/// Controller interface for devices with adjustable brightness.
/// </summary>
public interface IBrightnessController
{
    /// <summary>
    /// Sets the brightness level.
    /// </summary>
    /// <param name="brightness">Brightness level (0-100%).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Operation]
    Task SetBrightnessAsync(
        [OperationParameter(Unit = StateUnit.Percent)] decimal brightness,
        CancellationToken cancellationToken);
}
