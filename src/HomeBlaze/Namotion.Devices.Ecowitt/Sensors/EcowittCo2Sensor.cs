using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

[InterceptorSubject]
public partial class EcowittCo2Sensor :
    ITemperatureSensor,
    IHumiditySensor,
    IBatteryState,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    public int Channel { get; }

    [State]
    public partial decimal? Co2 { get; internal set; }

    [State]
    public partial decimal? Co2Avg24h { get; internal set; }

    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    [State(Unit = StateUnit.Percent)]
    public partial decimal? Humidity { get; internal set; }

    [State(Unit = StateUnit.Percent)]
    public partial decimal? BatteryLevel { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [State]
    public partial string? SensorId { get; internal set; }

    [State]
    public partial int? SignalStrength { get; internal set; }

    [Derived]
    public string? Title => $"CO₂ {Channel}";

    [Derived]
    public string IconName => "Co2";

    public string? IconColor => null;

    public EcowittCo2Sensor(int channel)
    {
        Channel = channel;
        Co2 = null;
        Co2Avg24h = null;
        Temperature = null;
        Humidity = null;
        BatteryLevel = null;
        LastUpdated = null;
        SensorId = null;
        SignalStrength = null;
    }
}
