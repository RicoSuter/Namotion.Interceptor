using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

[InterceptorSubject]
public partial class EcowittPm25Sensor :
    IPm25Sensor,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    public int Channel { get; }

    public partial decimal? Pm25 { get; internal set; }

    /// <summary>
    /// The average PM2.5 concentration of the last 24 hours in micrograms per cubic meter.
    /// </summary>
    [State(Unit = StateUnit.MicrogramPerCubicMeter, Position = 286)]
    public partial decimal? Pm25DailyAverage { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    /// <summary>
    /// Gets the paired sensors that can feed these readings, the same subjects as in the gateway's <c>Sensors</c>.
    /// </summary>
    [State]
    public partial Dictionary<string, EcowittSensor> Sources { get; internal set; }

    [Derived]
    public string? Title => $"PM2.5 {Channel}";

    [Derived]
    public string IconName => "Air";

    public string? IconColor => null;

    public EcowittPm25Sensor(int channel)
    {
        Channel = channel;
        Pm25 = null;
        Pm25DailyAverage = null;
        LastUpdated = null;
        Sources = new Dictionary<string, EcowittSensor>(StringComparer.Ordinal);
    }
}
