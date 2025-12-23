using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Light;

/// <summary>
/// Controller interface for devices with adjustable RGB color.
/// </summary>
public interface IColorController
{
    /// <summary>
    /// Sets the color.
    /// </summary>
    /// <param name="color">Color as hex string (e.g., "#FF0000").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Operation]
    Task SetColorAsync(
        [OperationParameter(Unit = StateUnit.HexColor)] string color,
        CancellationToken cancellationToken);
}
