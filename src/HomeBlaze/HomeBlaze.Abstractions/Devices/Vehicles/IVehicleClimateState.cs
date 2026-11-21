using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Vehicles;

/// <summary>
/// Climate state of a vehicle.
/// </summary>
[SubjectAbstraction]
[Description("Reports whether the climate of a vehicle runs and its inside and outside temperature.")]
public interface IVehicleClimateState
{
    /// <summary>
    /// Whether the climate control runs.
    /// </summary>
    [State(IsDiscrete = true, Position = 153)]
    [Category("Climate")]
    bool? IsClimateOn { get; }

    /// <summary>
    /// The temperature in the cabin, in degrees Celsius.
    /// </summary>
    [State(Unit = StateUnit.DegreeCelsius, Position = 201)]
    [Category("Climate")]
    decimal? InsideTemperature { get; }

    /// <summary>
    /// The temperature outside of the vehicle, in degrees Celsius.
    /// </summary>
    [State(Unit = StateUnit.DegreeCelsius, Position = 202)]
    [Category("Climate")]
    decimal? OutsideTemperature { get; }
}
