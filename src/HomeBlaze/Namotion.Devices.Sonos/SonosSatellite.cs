using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A unit bonded to a room player: a surround, a subwoofer or the second speaker of a stereo pair.
/// </summary>
[InterceptorSubject]
public partial class SonosSatellite : SonosDevice
{
    internal SonosSatellite(string uuid)
        : base(uuid)
    {
        Role = SonosSatelliteRole.Other;
    }

    [State(Position = 3)]
    public partial SonosSatelliteRole Role { get; internal set; }

    [Derived]
    public override string? IconName => Role == SonosSatelliteRole.Subwoofer ? "SurroundSound" : "Speaker";
}
