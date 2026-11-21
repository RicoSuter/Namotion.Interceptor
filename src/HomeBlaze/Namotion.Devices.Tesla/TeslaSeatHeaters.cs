using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// The seat and steering wheel heaters of a Tesla vehicle. <see cref="TeslaClimateState"/> sets the properties.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaSeatHeaters : ITitleProvider, IIconProvider
{
    public string Title => "Seat Heaters";

    public string IconName => "AirlineSeatReclineNormal";

    /// <summary>The heater level from 0 (off) to 3 (high).</summary>
    [State(Position = 1)] public partial int? DriverLevel { get; internal set; }

    /// <summary>The heater level from 0 (off) to 3 (high).</summary>
    [State(Position = 2)] public partial int? PassengerLevel { get; internal set; }

    /// <summary>The heater level from 0 (off) to 3 (high).</summary>
    [State(Position = 3)] public partial int? RearLeftLevel { get; internal set; }

    /// <summary>The heater level from 0 (off) to 3 (high).</summary>
    [State(Position = 4)] public partial int? RearCenterLevel { get; internal set; }

    /// <summary>The heater level from 0 (off) to 3 (high).</summary>
    [State(Position = 5)] public partial int? RearRightLevel { get; internal set; }

    [State(IsDiscrete = true, Position = 6)] public partial bool? IsAutoSeatClimateDriverOn { get; internal set; }
    [State(IsDiscrete = true, Position = 7)] public partial bool? IsAutoSeatClimatePassengerOn { get; internal set; }
    [State(IsDiscrete = true, Position = 8)] public partial bool? IsSteeringWheelHeaterOn { get; internal set; }

    /// <summary>The steering wheel heater level from 0 (off) to 2 (high).</summary>
    [State(Position = 9)] public partial int? SteeringWheelHeatLevel { get; internal set; }

    [State(IsDiscrete = true, Position = 10)] public partial bool? IsAutoSteeringWheelHeatOn { get; internal set; }

    public TeslaSeatHeaters()
    {
        DriverLevel = null;
        PassengerLevel = null;
        RearLeftLevel = null;
        RearCenterLevel = null;
        RearRightLevel = null;
        IsAutoSeatClimateDriverOn = null;
        IsAutoSeatClimatePassengerOn = null;
        IsSteeringWheelHeaterOn = null;
        SteeringWheelHeatLevel = null;
        IsAutoSteeringWheelHeatOn = null;
    }
}
