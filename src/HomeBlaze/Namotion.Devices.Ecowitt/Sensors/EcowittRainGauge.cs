using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

[InterceptorSubject]
public partial class EcowittRainGauge :
    IRainSensor,
    IBatteryState,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    private readonly string _name;

    [State(Unit = StateUnit.Millimeter)]
    public partial decimal? RainEvent { get; internal set; }

    [State(Unit = StateUnit.MillimeterPerHour)]
    public partial decimal? RainRate { get; internal set; }

    [State(Unit = StateUnit.Millimeter)]
    public partial decimal? HourlyRain { get; internal set; }

    [State(Unit = StateUnit.Millimeter)]
    public partial decimal? DailyRain { get; internal set; }

    [State(Unit = StateUnit.Millimeter)]
    public partial decimal? WeeklyRain { get; internal set; }

    [State(Unit = StateUnit.Millimeter)]
    public partial decimal? MonthlyRain { get; internal set; }

    [State(Unit = StateUnit.Millimeter)]
    public partial decimal? YearlyRain { get; internal set; }

    [State(Unit = StateUnit.Percent)]
    public partial decimal? BatteryLevel { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [State]
    public partial string? SensorId { get; internal set; }

    [State]
    public partial int? SignalStrength { get; internal set; }

    [State(Unit = StateUnit.Millimeter, IsCumulative = true)]
    public partial decimal? TotalRain { get; internal set; }

    [Derived]
    public bool? IsRaining => RainRate > 0;

    [Derived]
    public string? Title => _name;

    [Derived]
    public string IconName => IsRaining == true ? "Thunderstorm" : "WaterDrop";

    [Derived]
    public string? IconColor => IsRaining == true ? "Info" : null;

    public EcowittRainGauge(string name)
    {
        _name = name;
        RainEvent = null;
        RainRate = null;
        HourlyRain = null;
        DailyRain = null;
        WeeklyRain = null;
        MonthlyRain = null;
        YearlyRain = null;
        TotalRain = null;
        BatteryLevel = null;
        LastUpdated = null;
        SensorId = null;
        SignalStrength = null;
    }
}
