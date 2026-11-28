using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Abstractions.Devices.Energy;

/// <summary>
/// State interface for devices with a battery.
/// </summary>
[SubjectAbstraction]
[Description("Reports battery level as a percentage.")]
public interface IBatteryState
{
    /// <summary>
    /// The current battery level (0..1).
    /// </summary>
    [State(Unit = StateUnit.Percent, Position = 340)]
    decimal? BatteryLevel { get; }

    /// <summary>
    /// Whether the battery is low: by default true when <see cref="BatteryLevel"/> is at most 0.1, false above and null
    /// when the level is unknown. A device that reports a low battery flag implements this property itself and may leave
    /// <see cref="BatteryLevel"/> null when it reports no level.
    /// </summary>
    [Derived]
    [State(IsDiscrete = true, Position = 341)]
    bool? IsBatteryLow => BatteryLevel is { } level ? level <= 0.1m : null;
}
