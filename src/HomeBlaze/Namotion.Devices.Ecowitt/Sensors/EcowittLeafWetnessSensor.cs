using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices.Energy;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

[InterceptorSubject]
public partial class EcowittLeafWetnessSensor :
    IBatteryState,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    public int Channel { get; }

    [State(Unit = StateUnit.Percent)]
    public partial decimal? LeafWetness { get; internal set; }

    [State(Unit = StateUnit.Percent)]
    public partial decimal? BatteryLevel { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [State]
    public partial string? SensorId { get; internal set; }

    [State]
    public partial int? SignalStrength { get; internal set; }

    [Derived]
    public string? Title => $"Leaf {Channel}";

    [Derived]
    public string IconName => "Eco";

    public string? IconColor => null;

    public EcowittLeafWetnessSensor(int channel)
    {
        Channel = channel;
        LeafWetness = null;
        BatteryLevel = null;
        LastUpdated = null;
        SensorId = null;
        SignalStrength = null;
    }
}
