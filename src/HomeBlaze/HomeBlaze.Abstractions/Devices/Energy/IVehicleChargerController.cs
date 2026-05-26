namespace HomeBlaze.Abstractions.Devices.Energy;

/// <summary>
/// Controller interface for electric vehicle chargers.
/// </summary>
public interface IVehicleChargerController
{
    /// <summary>
    /// Pauses the current charging session.
    /// </summary>
    Task PauseChargingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resumes a paused charging session.
    /// </summary>
    Task ResumeChargingAsync(CancellationToken cancellationToken);
}
