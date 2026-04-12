using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices.Energy;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

[InterceptorSubject]
public partial class EcowittLightningSensor :
    IBatteryState,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    [State(Unit = StateUnit.Kilometer)]
    public partial decimal? Distance { get; internal set; }

    [State]
    public partial int? StrikeCount { get; internal set; }

    [State]
    public partial DateTimeOffset? LastStrikeTime { get; internal set; }

    [State(Unit = StateUnit.Percent)]
    public partial decimal? BatteryLevel { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [State]
    public partial string? SensorId { get; internal set; }

    [State]
    public partial int? SignalStrength { get; internal set; }

    [Derived]
    public string? Title => "Lightning";

    [Derived]
    public string IconName => "FlashOn";

    public string? IconColor => null;

    public EcowittLightningSensor()
    {
        Distance = null;
        StrikeCount = null;
        LastStrikeTime = null;
        BatteryLevel = null;
        LastUpdated = null;
        SensorId = null;
        SignalStrength = null;
    }
}
