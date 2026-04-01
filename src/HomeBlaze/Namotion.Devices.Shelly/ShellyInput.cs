using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Shelly;

[InterceptorSubject]
public partial class ShellyInput :
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    internal int Index { get; }

    [State(IsDiscrete = true)]
    public partial bool? State { get; internal set; }

    [State]
    public partial long? CountTotal { get; internal set; }

    [State(Unit = StateUnit.Hertz)]
    public partial double? CountFrequency { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? Title => $"Input {Index}";

    [Derived]
    public string IconName => CountTotal != null ? "Speed" : "Input";

    [Derived]
    public string? IconColor => State == true ? "Success" : null;

    [Derived]
    public bool IsCounterInput => CountTotal != null;

    public ShellyInput(int index)
    {
        Index = index;
        State = null;
        CountTotal = null;
        CountFrequency = null;
        LastUpdated = null;
    }
}
