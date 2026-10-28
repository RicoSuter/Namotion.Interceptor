using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

[InterceptorSubject]
public partial class EcowittLightningSensor :
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    [State(Unit = StateUnit.Kilometer)]
    public partial decimal? Distance { get; internal set; }

    [State]
    public partial int? StrikeCount { get; internal set; }

    [State]
    public partial DateTimeOffset? LastStrikeTime { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    /// <summary>
    /// Gets the paired sensors that can feed these readings, the same subjects as in the gateway's <c>Sensors</c>.
    /// </summary>
    [State]
    public partial Dictionary<string, EcowittSensor> Sources { get; internal set; }

    [Derived]
    public string? Title => "Lightning";

    [Derived]
    public string IconName => "FlashOn";

    public string? IconColor => null;

    public EcowittLightningSensor()
    {
        Distance = null;
        StrikeCount = null;
        LastStrikeTime = null;
        LastUpdated = null;
        Sources = new Dictionary<string, EcowittSensor>(StringComparer.Ordinal);
    }
}
