using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

[InterceptorSubject]
public partial class EcowittChannelSensor :
    ITemperatureSensor,
    IHumiditySensor,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    public int Channel { get; }

    [State]
    public partial string? Name { get; internal set; }

    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    [State(Unit = StateUnit.Percent)]
    public partial decimal? Humidity { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    /// <summary>
    /// Gets the paired sensors that can feed these readings, the same subjects as in the gateway's <c>Sensors</c>.
    /// </summary>
    [State]
    public partial Dictionary<string, EcowittSensor> Sources { get; internal set; }

    [Derived]
    public string? Title => !string.IsNullOrEmpty(Name) ? Name : $"Channel {Channel}";

    [Derived]
    public string IconName => "Thermostat";

    public string? IconColor => null;

    public EcowittChannelSensor(int channel)
    {
        Channel = channel;
        Name = null;
        Temperature = null;
        Humidity = null;
        LastUpdated = null;
        Sources = new Dictionary<string, EcowittSensor>(StringComparer.Ordinal);
    }
}
