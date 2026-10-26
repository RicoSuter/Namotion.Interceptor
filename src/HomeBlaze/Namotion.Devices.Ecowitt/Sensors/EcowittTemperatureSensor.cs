using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

[InterceptorSubject]
public partial class EcowittTemperatureSensor :
    ITemperatureSensor,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    public int Channel { get; }

    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    /// <summary>
    /// Gets the paired sensors that can feed these readings, the same subjects as in the gateway's <c>Sensors</c>.
    /// </summary>
    [State]
    public partial Dictionary<string, EcowittSensor> Sources { get; internal set; }

    [Derived]
    public string? Title => $"Temperature {Channel}";

    [Derived]
    public string IconName => "DeviceThermostat";

    public string? IconColor => null;

    public EcowittTemperatureSensor(int channel)
    {
        Channel = channel;
        Temperature = null;
        LastUpdated = null;
        Sources = new Dictionary<string, EcowittSensor>(StringComparer.Ordinal);
    }
}
