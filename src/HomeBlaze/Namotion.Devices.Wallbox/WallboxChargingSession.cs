using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Wallbox;

[InterceptorSubject]
public partial class WallboxChargingSession : ITitleProvider, IIconProvider
{
    [State(Position = 1, Unit = StateUnit.WattHour)]
    public partial decimal? AddedEnergy { get; internal set; }

    [State(Position = 2, Unit = StateUnit.WattHour)]
    public partial decimal? AddedGreenEnergy { get; internal set; }

    [State(Position = 3, Unit = StateUnit.WattHour)]
    public partial decimal? AddedGridEnergy { get; internal set; }

    [State(Position = 4)]
    public partial decimal? AddedRange { get; internal set; }

    [State(Position = 5)]
    public partial TimeSpan? ChargingTime { get; internal set; }

    [State(Position = 6)]
    public partial decimal? SessionCost { get; internal set; }

    [State(Position = 7, Unit = StateUnit.Percent)]
    public partial decimal? ChargeLevel { get; internal set; }

    [Derived]
    public string? Title => "Session";

    [Derived]
    public string IconName => "BatteryChargingFull";

    public string? IconColor => null;

    public WallboxChargingSession()
    {
        AddedEnergy = null;
        AddedGreenEnergy = null;
        AddedGridEnergy = null;
        AddedRange = null;
        ChargingTime = null;
        SessionCost = null;
        ChargeLevel = null;
    }
}
