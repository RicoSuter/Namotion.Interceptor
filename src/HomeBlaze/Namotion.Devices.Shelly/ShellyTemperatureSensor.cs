using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Shelly;

[InterceptorSubject]
public partial class ShellyTemperatureSensor :
    ITemperatureSensor,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    internal int Index { get; }

    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? Title => $"Temperature {Index}";

    [Derived]
    public string IconName => "Thermostat";

    public string? IconColor => null;

    public ShellyTemperatureSensor(int index)
    {
        Index = index;
        Temperature = null;
        LastUpdated = null;
    }
}
