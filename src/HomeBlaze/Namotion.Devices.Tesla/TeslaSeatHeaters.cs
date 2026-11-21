using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// The seat and steering wheel heaters of a Tesla vehicle. Left and right are physical sides, not relative to the driver. <see cref="TeslaClimateState"/> sets the properties.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaSeatHeaters : ITitleProvider, IIconProvider
{
    public string Title => "Seat Heaters";

    public string IconName => "AirlineSeatReclineNormal";

    /// <summary>The heater level from 0 (off) to 3 (high).</summary>
    [State(Position = 1)] public partial int? FrontLeftLevel { get; internal set; }

    /// <summary>The heater level from 0 (off) to 3 (high).</summary>
    [State(Position = 2)] public partial int? FrontRightLevel { get; internal set; }

    /// <summary>The heater level from 0 (off) to 3 (high).</summary>
    [State(Position = 3)] public partial int? RearLeftLevel { get; internal set; }

    /// <summary>The heater level from 0 (off) to 3 (high).</summary>
    [State(Position = 4)] public partial int? RearCenterLevel { get; internal set; }

    /// <summary>The heater level from 0 (off) to 3 (high).</summary>
    [State(Position = 5)] public partial int? RearRightLevel { get; internal set; }

    [State(IsDiscrete = true, Position = 6)] public partial bool? IsAutoSeatClimateFrontLeftOn { get; internal set; }
    [State(IsDiscrete = true, Position = 7)] public partial bool? IsAutoSeatClimateFrontRightOn { get; internal set; }
    [State(IsDiscrete = true, Position = 8)] public partial bool? IsSteeringWheelHeaterOn { get; internal set; }

    /// <summary>The steering wheel heater level from 0 (off) to 2 (high).</summary>
    [State(Position = 9)] public partial int? SteeringWheelHeatLevel { get; internal set; }

    [State(IsDiscrete = true, Position = 10)] public partial bool? IsAutoSteeringWheelHeatOn { get; internal set; }

    public TeslaSeatHeaters()
    {
        FrontLeftLevel = null;
        FrontRightLevel = null;
        RearLeftLevel = null;
        RearCenterLevel = null;
        RearRightLevel = null;
        IsAutoSeatClimateFrontLeftOn = null;
        IsAutoSeatClimateFrontRightOn = null;
        IsSteeringWheelHeaterOn = null;
        SteeringWheelHeatLevel = null;
        IsAutoSteeringWheelHeatOn = null;
    }
}
