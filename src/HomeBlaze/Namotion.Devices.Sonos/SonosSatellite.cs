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

    /// <summary>
    /// The model, the room of the owning player and the role, since the units of one room often share a model.
    /// </summary>
    [Derived]
    public override string? Title => $"{Model ?? "Sonos"} ({RoomName}, {GetRoleName(Role)})";

    [Derived]
    public override string? IconName => Role == SonosSatelliteRole.Subwoofer ? "SurroundSound" : "Speaker";

    private static string GetRoleName(SonosSatelliteRole role) => role switch
    {
        SonosSatelliteRole.Subwoofer => "subwoofer",
        SonosSatelliteRole.RearLeft => "rear left",
        SonosSatelliteRole.RearRight => "rear right",
        SonosSatelliteRole.StereoPartner => "stereo partner",
        _ => "satellite"
    };
}
