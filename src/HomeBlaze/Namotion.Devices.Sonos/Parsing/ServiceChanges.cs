namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// AVTransport values from an event or a poll. A null field was not reported. String fields are raw wire values, which may be
/// <c>NOT_IMPLEMENTED</c> or empty, and consumers filter them with <see cref="SonosValues.IsKnown"/>. The media metadata
/// is the DIDL of the media the player was told to play, which names the station or playlist.
/// </summary>
internal sealed record AvTransportChange(
    string? TransportState,
    string? PlayMode,
    string? MediaUri,
    string? TrackUri,
    string? TrackDuration,
    string? TrackMetaData,
    string? MediaMetaData = null);

/// <summary>
/// RenderingControl values on the Master channel. A null field was not reported or not parsable.
/// </summary>
internal sealed record RenderingControlChange(
    int? Volume,
    bool? Mute,
    int? Bass,
    int? Treble,
    bool? Loudness,
    bool? NightMode,
    bool? SpeechEnhancement);

/// <summary>
/// GroupRenderingControl values read from a group coordinator.
/// </summary>
internal sealed record GroupRenderingControlChange(int? Volume, bool? Mute);
