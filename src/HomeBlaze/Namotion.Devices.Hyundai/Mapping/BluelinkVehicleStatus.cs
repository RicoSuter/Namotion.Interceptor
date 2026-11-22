using HomeBlaze.Abstractions.Devices.Locks;

namespace Namotion.Devices.Hyundai.Mapping;

/// <summary>A tire of the status: the low pressure warning and the pressure in bar.</summary>
internal sealed record BluelinkTireStatus(bool? IsPressureLow, decimal? Pressure);

/// <summary>
/// The values of one stored status, in HomeBlaze units: fractions from 0 to 1, km, W, °C, bar. Null when not reported.
/// Hyundai reports the rear doors and windows as left and right; the left ones are the driver rear ones, which assumes a
/// left-hand drive vehicle. Tires keep the physical sides.
/// </summary>
internal sealed record BluelinkVehicleStatus
{
    private static readonly BluelinkTireStatus UnknownTire = new(null, null);

    public DateTimeOffset? Time { get; init; }
    public decimal? TotalDrivenDistance { get; init; }
    public decimal? AuxiliaryBatteryLevel { get; init; }

    public decimal? BatteryLevel { get; init; }
    public bool? IsPluggedIn { get; init; }
    public bool? IsCharging { get; init; }
    public decimal? ChargingPower { get; init; }
    public TimeSpan? RemainingChargingTime { get; init; }
    public decimal? ChargeLimit { get; init; }
    public decimal? FastChargeLimit { get; init; }
    public decimal? Range { get; init; }

    public bool? IsClimateOn { get; init; }
    public decimal? DriverTemperatureSetting { get; init; }
    public decimal? OutsideTemperature { get; init; }
    public bool? IsFrontDefrosterOn { get; init; }
    public bool? IsSteeringWheelHeaterOn { get; init; }

    public DoorLockState? LockState { get; init; }
    public bool? IsDriverFrontDoorOpen { get; init; }
    public bool? IsPassengerFrontDoorOpen { get; init; }
    public bool? IsDriverRearDoorOpen { get; init; }
    public bool? IsPassengerRearDoorOpen { get; init; }
    public bool? IsTrunkOpen { get; init; }
    public bool? IsHoodOpen { get; init; }
    public bool? IsDriverFrontWindowOpen { get; init; }
    public bool? IsPassengerFrontWindowOpen { get; init; }
    public bool? IsDriverRearWindowOpen { get; init; }
    public bool? IsPassengerRearWindowOpen { get; init; }

    public BluelinkTireStatus FrontLeftTire { get; init; } = UnknownTire;
    public BluelinkTireStatus FrontRightTire { get; init; } = UnknownTire;
    public BluelinkTireStatus RearLeftTire { get; init; } = UnknownTire;
    public BluelinkTireStatus RearRightTire { get; init; } = UnknownTire;
}
