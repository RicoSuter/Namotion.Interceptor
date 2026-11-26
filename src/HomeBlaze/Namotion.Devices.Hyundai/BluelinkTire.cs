using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Hyundai.Mapping;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Hyundai;

/// <summary>A tire of a Bluelink vehicle.</summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class BluelinkTire : ITitleProvider, IIconProvider
{
    internal BluelinkTire(string title)
    {
        Title = title;
        IsPressureLow = null;
        Pressure = null;
    }

    public string Title { get; }

    public string IconName => "TireRepair";

    /// <summary>Whether the vehicle warns about a low pressure of this tire.</summary>
    [State(IsDiscrete = true, Position = 1)] public partial bool? IsPressureLow { get; internal set; }

    /// <summary>The pressure, or null when the vehicle has no direct pressure sensor.</summary>
    [State(Unit = StateUnit.Bar, Position = 2)] public partial decimal? Pressure { get; internal set; }

    internal void Apply(BluelinkTireStatus status)
    {
        IsPressureLow = status.IsPressureLow;
        Pressure = status.Pressure;
    }
}
