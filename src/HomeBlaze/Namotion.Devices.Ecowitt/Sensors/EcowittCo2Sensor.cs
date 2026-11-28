using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

[InterceptorSubject]
public partial class EcowittCo2Sensor :
    ICarbonDioxideSensor,
    ITemperatureSensor,
    IHumiditySensor,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    public int Channel { get; }

    public partial decimal? CarbonDioxide { get; internal set; }

    /// <summary>
    /// The average carbon dioxide concentration of the last 24 hours in parts per million.
    /// </summary>
    [State(Unit = StateUnit.PartsPerMillion, Position = 400)]
    public partial decimal? CarbonDioxideDailyAverage { get; internal set; }

    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    [State(Unit = StateUnit.Percent)]
    public partial decimal? Humidity { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? Title => $"CO₂ {Channel}";

    [Derived]
    public string IconName => "Co2";

    public string? IconColor => null;

    public EcowittCo2Sensor(int channel)
    {
        Channel = channel;
        CarbonDioxide = null;
        CarbonDioxideDailyAverage = null;
        Temperature = null;
        Humidity = null;
        LastUpdated = null;
    }
}
