using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Wallbox;

[InterceptorSubject]
public partial class WallboxChargingSession : ITitleProvider, IIconProvider
{
    [State(IsDiscrete = true, Position = 0)]
    public partial bool IsCharging { get; internal set; }

    [State(Unit = StateUnit.WattHour, Position = 1)]
    public partial decimal? AddedEnergy { get; internal set; }

    [State(Unit = StateUnit.WattHour, Position = 2)]
    public partial decimal? AddedGreenEnergy { get; internal set; }

    [State(Unit = StateUnit.WattHour, Position = 3)]
    public partial decimal? AddedGridEnergy { get; internal set; }

    [State(Unit = StateUnit.Kilometer, Position = 4)]
    public partial decimal? AddedRange { get; internal set; }

    [State(Position = 5)]
    public partial TimeSpan? ChargingTime { get; internal set; }

    [State(Position = 6)]
    public partial decimal? SessionCost { get; internal set; }

    [State(Unit = StateUnit.Percent, Position = 7)]
    public partial decimal? ChargeLevel { get; internal set; }

    [Derived]
    public string? Title => "Session";

    [Derived]
    public string IconName => "BatteryChargingFull";

    public string? IconColor => null;

    public WallboxChargingSession()
    {
        IsCharging = false;
        AddedEnergy = null;
        AddedGreenEnergy = null;
        AddedGridEnergy = null;
        AddedRange = null;
        ChargingTime = null;
        SessionCost = null;
        ChargeLevel = null;
    }
}
