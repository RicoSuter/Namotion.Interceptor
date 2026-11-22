using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Vehicles;

/// <summary>
/// Controls the charging of a vehicle. Implemented by the subject that implements <see cref="IVehicleChargingState"/>.
/// Implementations throw <see cref="InvalidOperationException"/> when a command cannot be sent now, for example while the vehicle is not connected.
/// Chargers, such as wallboxes, implement <see cref="Energy.IVehicleChargerController"/> (pause and resume) instead.
/// </summary>
[SubjectAbstraction]
[Description("Starts and stops the charging of a vehicle and sets its charge limit.")]
public interface IVehicleChargingController
{
    /// <summary>Starts charging while the vehicle is plugged in.</summary>
    /// <exception cref="InvalidOperationException">The command cannot be sent now.</exception>
    [Operation(Title = "Start Charging", Icon = "EvStation", Position = 1)]
    [Category("Charging")]
    Task StartChargingAsync(CancellationToken cancellationToken);

    /// <summary>Stops charging.</summary>
    /// <exception cref="InvalidOperationException">The command cannot be sent now.</exception>
    [Operation(Title = "Stop Charging", Icon = "Stop", Position = 2)]
    [Category("Charging")]
    Task StopChargingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sets the battery level at which charging stops (0..1), the value of <see cref="IVehicleChargingState.ChargeLimit"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The vehicle cannot set this limit.</exception>
    /// <exception cref="InvalidOperationException">The command cannot be sent now.</exception>
    [Operation(Title = "Set Charge Limit", Icon = "BatteryChargingFull", Position = 3)]
    [Category("Charging")]
    Task SetChargeLimitAsync([OperationParameter(Unit = StateUnit.Percent)] decimal chargeLimit, CancellationToken cancellationToken);
}
