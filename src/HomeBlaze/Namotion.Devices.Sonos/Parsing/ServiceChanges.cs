namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// AVTransport values from an event or a poll. A null field was not reported. String fields are raw wire values, which may be
/// <c>NOT_IMPLEMENTED</c> or empty, and consumers filter them with <see cref="SonosValues.IsKnown"/>.
/// </summary>
internal sealed record AvTransportChange(
    string? TransportState,
    string? PlayMode,
    string? MediaUri,
    string? TrackUri,
    string? TrackDuration,
    string? TrackMetaData);

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

/// <summary>
/// One poll of a player. Position and sleep timer are never evented, so they come only from here.
/// </summary>
internal sealed record SonosPlayerReading(
    AvTransportChange AvTransport,
    TimeSpan? Position,
    TimeSpan? SleepTimerRemaining,
    RenderingControlChange RenderingControl);
