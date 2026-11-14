using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

[InterceptorSubject]
public partial class EcowittLeafWetnessSensor :
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    public int Channel { get; }

    [State(Unit = StateUnit.Percent)]
    public partial decimal? LeafWetness { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    /// <summary>
    /// Gets the paired sensors that can feed these readings, the same subjects as in the gateway's <c>Sensors</c>.
    /// </summary>
    [State]
    public partial Dictionary<string, EcowittSensor> Sources { get; internal set; }

    [Derived]
    public string? Title => $"Leaf {Channel}";

    [Derived]
    public string IconName => "Eco";

    public string? IconColor => null;

    public EcowittLeafWetnessSensor(int channel)
    {
        Channel = channel;
        LeafWetness = null;
        LastUpdated = null;
        Sources = new Dictionary<string, EcowittSensor>(StringComparer.Ordinal);
    }
}
