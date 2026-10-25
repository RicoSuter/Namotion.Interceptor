using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

[InterceptorSubject]
public partial class EcowittLeakSensor :
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    public int Channel { get; }

    [State(IsDiscrete = true)]
    public partial bool? IsLeaking { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    /// <summary>
    /// Gets the paired sensors that can feed these readings, the same subjects as in the gateway's <c>Sensors</c>.
    /// </summary>
    [State]
    public partial Dictionary<string, EcowittSensor> Sources { get; internal set; }

    [Derived]
    public string? Title => $"Leak {Channel}";

    [Derived]
    public string IconName => IsLeaking == true ? "WaterDamage" : "Water";

    [Derived]
    public string? IconColor => IsLeaking == true ? "Error" : null;

    public EcowittLeakSensor(int channel)
    {
        Channel = channel;
        IsLeaking = null;
        LastUpdated = null;
        Sources = new Dictionary<string, EcowittSensor>(StringComparer.Ordinal);
    }
}
