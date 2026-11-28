using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Abstractions.Devices.Energy;

/// <summary>
/// State interface for devices with a battery.
/// </summary>
[SubjectAbstraction]
[Description("Reports battery level as a percentage and whether the battery is low.")]
public interface IBatteryState
{
    /// <summary>
    /// The battery level at or below which the default <see cref="IsBatteryLow"/> reports a low battery.
    /// </summary>
    public const decimal LowBatteryLevel = 0.1m;

    /// <summary>
    /// The current battery level (0..1).
    /// </summary>
    [State(Unit = StateUnit.Percent, Position = 340)]
    decimal? BatteryLevel { get; }

    /// <summary>
    /// Whether the battery that powers the device needs charging or replacing soon: by default true when
    /// <see cref="BatteryLevel"/> is at most <see cref="LowBatteryLevel"/>, false above and null when the level is unknown. A device that reports
    /// a low battery flag implements this property itself and may leave <see cref="BatteryLevel"/> null when it reports
    /// no level. A stationary storage battery that is discharged to a reserve by design reports null.
    /// </summary>
    [Derived]
    [State(IsDiscrete = true, Position = 344)]
    bool? IsBatteryLow => BatteryLevel is { } level ? level <= LowBatteryLevel : null;
}
