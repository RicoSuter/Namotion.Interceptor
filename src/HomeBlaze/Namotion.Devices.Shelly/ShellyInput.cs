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

    [State(IsDiscrete = true, Position = 100)]
    public partial bool? IsActive { get; internal set; }

    [State(IsCumulative = true, Position = 400)]
    public partial long? TotalCount { get; internal set; }

    [State(Unit = StateUnit.Hertz, Position = 401)]
    public partial double? CountFrequency { get; internal set; }

    [State(Position = 950)]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? Title => $"Input {Index}";

    [Derived]
    public string IconName => TotalCount != null ? "Speed" : "Input";

    [Derived]
    public string? IconColor => IsActive == true ? "Success" : null;

    [Derived]
    public bool IsCounterInput => TotalCount != null;

    public ShellyInput(int index)
    {
        Index = index;
        IsActive = null;
        TotalCount = null;
        CountFrequency = null;
        LastUpdated = null;
    }
}
