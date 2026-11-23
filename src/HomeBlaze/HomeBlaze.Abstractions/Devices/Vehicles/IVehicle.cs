using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices.Locks;

namespace HomeBlaze.Abstractions.Devices.Vehicles;

/// <summary>
/// A vehicle with its driven distance, its location and the subjects that report its climate and its door locks.
/// </summary>
[SubjectAbstraction]
[Description("Vehicle with driven distance, location, climate and door locks.")]
public interface IVehicle
{
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

    /// <summary>
    /// The climate of the vehicle, or null when the vehicle does not report it.
    /// </summary>
    IVehicleClimateState? Climate { get; }

    /// <summary>
    /// The door locks of the vehicle, or null when the vehicle does not report them.
    /// </summary>
    IDoorLockState? Body { get; }
}
