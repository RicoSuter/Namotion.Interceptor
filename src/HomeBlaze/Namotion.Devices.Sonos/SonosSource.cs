namespace Namotion.Devices.Sonos;

/// <summary>
/// Where the audio of a Sonos player comes from. An unknown source is <c>null</c>, not a member.
/// </summary>
public enum SonosSource
{
    /// <summary>
    /// Nothing is loaded.
    /// </summary>
    None,
    Tv,
    LineIn,
    SpotifyConnect,
    AirPlay,
    Radio,
    Queue,

    /// <summary>
    /// A source without a member of its own, which a later version may refine into one, such as Bluetooth.
    /// </summary>
    Other
}
