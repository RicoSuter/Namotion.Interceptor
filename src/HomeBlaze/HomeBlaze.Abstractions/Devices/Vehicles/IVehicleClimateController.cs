using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Vehicles;

/// <summary>
/// Controls the climate of a vehicle. Implemented by the subject that implements <see cref="IVehicleClimateState"/>.
/// </summary>
[SubjectAbstraction]
[Description("Starts and stops the climate control of a vehicle.")]
public interface IVehicleClimateController
{
    /// <summary>Starts the climate control with the given cabin temperature in degrees Celsius.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The vehicle cannot set this temperature.</exception>
    [Operation(Title = "Start Climate", Icon = "Thermostat", Position = 1)]
    [Category("Climate")]
    Task StartClimateAsync(decimal temperature, CancellationToken cancellationToken);

    /// <summary>Stops the climate control.</summary>
    [Operation(Title = "Stop Climate", Icon = "Stop", Position = 2)]
    [Category("Climate")]
    Task StopClimateAsync(CancellationToken cancellationToken);
}
