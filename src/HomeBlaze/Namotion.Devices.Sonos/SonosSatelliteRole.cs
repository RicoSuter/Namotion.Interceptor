namespace Namotion.Devices.Sonos;

/// <summary>
/// The role a bonded unit plays for its room player.
/// </summary>
public enum SonosSatelliteRole
{
    /// <summary>
    /// A role without a member of its own, which a later version may refine into one.
    /// </summary>
    Other,
    Subwoofer,
    RearLeft,
    RearRight,
    StereoPartner
}
