namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// AVTransport values from an event or a poll. A null field was not reported and keeps the current value.
/// </summary>
internal sealed record AvTransportChange(
    string? TransportState,
    string? PlayMode,
    string? MediaUri,
    string? TrackUri,
    string? TrackDuration,
    string? TrackMetaData);

/// <summary>
/// RenderingControl values on the Master channel. A null field was not reported and keeps the current value.
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

/// <summary>
/// One poll of a player. Position and sleep timer are never evented, so they come only from here.
/// </summary>
internal sealed record SonosPlayerReading(
    AvTransportChange AvTransport,
    TimeSpan? Position,
    TimeSpan? SleepTimerRemaining,
    RenderingControlChange RenderingControl);
