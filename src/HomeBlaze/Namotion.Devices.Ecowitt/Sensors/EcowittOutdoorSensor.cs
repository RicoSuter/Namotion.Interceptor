using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

[InterceptorSubject]
public partial class EcowittOutdoorSensor :
    ITemperatureSensor,
    IHumiditySensor,
    IWindSensor,
    IUvIndexSensor,
    ILightSensor,
    IBatteryState,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    [State(Unit = StateUnit.Percent)]
    public partial decimal? Humidity { get; internal set; }

    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? DewPoint { get; internal set; }

    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? FeelsLikeTemperature { get; internal set; }

    [State(Unit = StateUnit.MetersPerSecond)]
    public partial decimal? WindSpeed { get; internal set; }

    [State(Unit = StateUnit.MetersPerSecond)]
    public partial decimal? WindGust { get; internal set; }

    [State(Unit = StateUnit.MetersPerSecond)]
    public partial decimal? MaxDailyGust { get; internal set; }

    [State(Unit = StateUnit.Degree)]
    public partial decimal? WindDirection { get; internal set; }

    [State(Unit = StateUnit.Lux)]
    public partial decimal? Illuminance { get; internal set; }

    [State(Unit = StateUnit.UvIndex)]
    public partial decimal? UvIndex { get; internal set; }

    [State]
    public partial decimal? SolarRadiation { get; internal set; }

    [State]
    public partial decimal? VaporPressureDeficit { get; internal set; }

    [State(Unit = StateUnit.Percent)]
    public partial decimal? BatteryLevel { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [State]
    public partial string? SensorId { get; internal set; }

    [State]
    public partial int? SignalStrength { get; internal set; }

    [Derived]
    public string? Title => "Outdoor";

    [Derived]
    public string IconName => "WbSunny";

    public string? IconColor => null;

    public EcowittOutdoorSensor()
    {
        Temperature = null;
        Humidity = null;
        DewPoint = null;
        FeelsLikeTemperature = null;
        WindSpeed = null;
        WindGust = null;
        MaxDailyGust = null;
        WindDirection = null;
        Illuminance = null;
        UvIndex = null;
        SolarRadiation = null;
        VaporPressureDeficit = null;
        BatteryLevel = null;
        LastUpdated = null;
        SensorId = null;
        SignalStrength = null;
    }
}
