using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;

namespace HomeBlaze.Abstractions.Devices.Vehicles;

/// <summary>
/// State of a vehicle: range, driven distance and location.
/// </summary>
[SubjectAbstraction]
[Description("Reports the range, the driven distance and the location of a vehicle.")]
public interface IVehicleState
{
    /// <summary>
    /// The estimated distance the vehicle can still drive, in kilometers.
    /// </summary>
    [State(Unit = StateUnit.Kilometer, Position = 223)]
    [Category("Vehicle")]
    decimal? Range { get; }

    /// <summary>
    /// The distance the vehicle has driven in its life (the odometer), in kilometers.
    /// </summary>
    [State(Unit = StateUnit.Kilometer, IsCumulative = true, Position = 224)]
    [Category("Vehicle")]
    decimal? TotalDrivenDistance { get; }

    /// <summary>
    /// The location of the vehicle.
    /// </summary>
    [State(Position = 225)]
    [Category("Vehicle")]
    GeoCoordinate? Location { get; }
}
