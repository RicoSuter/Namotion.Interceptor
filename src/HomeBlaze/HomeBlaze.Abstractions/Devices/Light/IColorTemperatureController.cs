using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Light;

/// <summary>
/// Controller interface for devices with adjustable color temperature.
/// </summary>
public interface IColorTemperatureController
{
    /// <summary>
    /// Sets the color temperature.
    /// </summary>
    /// <param name="colorTemperature">Color temperature (0 = warm, 1 = cool).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Operation]
    Task SetColorTemperatureAsync(
        [OperationParameter(Unit = StateUnit.Percent)] decimal colorTemperature,
        CancellationToken cancellationToken);
}
