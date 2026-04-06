namespace HomeBlaze.Abstractions.Devices.Energy;

/// <summary>
/// Controller interface for electric vehicle chargers.
/// </summary>
public interface IVehicleChargerController
{
    Task PauseChargingAsync(CancellationToken cancellationToken);
    Task ResumeChargingAsync(CancellationToken cancellationToken);
}
