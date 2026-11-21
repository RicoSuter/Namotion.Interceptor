using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// One tire of a Tesla vehicle. <see cref="TeslaVehicleBody"/> sets the properties.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaTire : ITitleProvider, IIconProvider
{
    public string Title { get; }

    public string IconName => "TireRepair";

    [State(Unit = StateUnit.Bar, Position = 1)] public partial decimal? Pressure { get; internal set; }

    /// <summary>The pressure Tesla recommends for this axle.</summary>
    [State(Unit = StateUnit.Bar, Position = 2)] public partial decimal? RecommendedPressure { get; internal set; }

    [State(IsDiscrete = true, Position = 3)] public partial bool? HasSoftWarning { get; internal set; }
    [State(IsDiscrete = true, Position = 4)] public partial bool? HasHardWarning { get; internal set; }

    /// <summary>The time of the last pressure reading of the tire sensor.</summary>
    [State(Position = 5)] public partial DateTimeOffset? LastSeen { get; internal set; }

    public TeslaTire(string title)
    {
        Title = title;
        Pressure = null;
        RecommendedPressure = null;
        HasSoftWarning = null;
        HasHardWarning = null;
        LastSeen = null;
    }
}
