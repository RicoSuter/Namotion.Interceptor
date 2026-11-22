using CarServer;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Media;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// The media player of a Tesla vehicle.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaMedia : ITitleProvider, IIconProvider, IMediaTrackState, IVolumeState
{
    // The cached snapshot has separate timestamps for the player state and for the track details, the JSON has one for both.
    private readonly ILogger _logger;
    private readonly TeslaSectionClock _stateClock = new();
    private readonly TeslaSectionClock _detailClock = new();

    // Tesla's own volume scale, which ends at the maximum. A source that sends the volume without the maximum keeps the last known maximum.
    private decimal? _audioVolume;
    private decimal? _audioVolumeMaximum;

    public string Title => "Media";

    public string IconName => "MusicNote";

    [State(IsDiscrete = true, Position = 1)] public partial TeslaMediaPlaybackStatus? PlaybackStatus { get; internal set; }

    /// <summary>The name of the media source, such as "Spotify".</summary>
    [State(Position = 2)] public partial string? Source { get; internal set; }

    public partial string? CurrentTrackTitle { get; internal set; }

    public partial string? CurrentTrackArtist { get; internal set; }

    public partial string? CurrentTrackAlbum { get; internal set; }

    /// <summary>The name of the radio station that is playing.</summary>
    [State(Position = 3)] public partial string? Station { get; internal set; }

    /// <summary>Tesla does not report the track duration of a stream.</summary>
    public partial TimeSpan? CurrentTrackDuration { get; internal set; }

    public partial TimeSpan? CurrentTrackPosition { get; internal set; }

    [State(IsDiscrete = true, Position = 4)] public partial bool? IsRemoteControlEnabled { get; internal set; }

    /// <summary>The volume as a fraction of the maximum, from 0 to 1. Null while the maximum is unknown or 0.</summary>
    [State(Unit = StateUnit.Percent, Position = 5)] public partial decimal? Volume { get; internal set; }

    /// <summary>Tesla does not report whether the audio is muted.</summary>
    public bool? IsMuted => null;

    public string? CurrentTrackImageUri => null;

    public string? CurrentTrackUri => null;

    internal TeslaMedia(ILogger logger)
    {
        _logger = logger;
        PlaybackStatus = null;
        Source = null;
        CurrentTrackTitle = null;
        CurrentTrackArtist = null;
        CurrentTrackAlbum = null;
        Station = null;
        CurrentTrackDuration = null;
        CurrentTrackPosition = null;
        IsRemoteControlEnabled = null;
        Volume = null;
    }

    internal void Update(TeslaVehicleStateDto dto)
    {
        // Both clocks advance, so no check may short-circuit the other.
        var timestamp = TeslaValues.FromUnixMilliseconds(dto.Timestamp);
        var isStateNewer = _stateClock.TryAdvance(timestamp);
        var isDetailNewer = _detailClock.TryAdvance(timestamp);

        if (isStateNewer)
        {
            UpdateStateFromJson(dto);
        }

        if (isDetailNewer && dto.MediaInfo is { } info)
        {
            UpdateDetailsFromJson(info);
        }
    }

    internal void Update(MediaState proto)
    {
        if (!_stateClock.TryAdvance(proto.Timestamp?.ToDateTimeOffset()))
        {
            return;
        }

        UpdateTrackFromProtobuf(proto);
        UpdateVolumeFromProtobuf(proto);
    }

    internal void Update(MediaDetailState proto)
    {
        if (!_detailClock.TryAdvance(proto.Timestamp?.ToDateTimeOffset()))
        {
            return;
        }

        UpdateDetailsFromProtobuf(proto);
    }

    // The JSON volumes have more digits than a protobuf float, so they are rounded to float precision to give the same value.
    private void UpdateStateFromJson(TeslaVehicleStateDto dto)
    {
        IsRemoteControlEnabled = dto.MediaState?.RemoteControlEnabled;

        if (dto.MediaInfo is { } info)
        {
            PlaybackStatus = TeslaValues.Enum<TeslaMediaPlaybackStatus>(info.MediaPlaybackStatus, _logger);
            CurrentTrackTitle = TeslaValues.Text(info.NowPlayingTitle);
            CurrentTrackArtist = TeslaValues.Text(info.NowPlayingArtist);
            _audioVolume = TeslaValues.ToFloatPrecision(info.AudioVolume);
            _audioVolumeMaximum = TeslaValues.ToFloatPrecision(info.AudioVolumeMax) ?? _audioVolumeMaximum;
            UpdateVolume();
        }
    }

    private void UpdateDetailsFromJson(TeslaMediaInfoDto info)
    {
        Source = TeslaValues.Text(info.NowPlayingSource);
        CurrentTrackAlbum = TeslaValues.Text(info.NowPlayingAlbum);
        Station = TeslaValues.Text(info.NowPlayingStation);
        CurrentTrackDuration = Duration(info.NowPlayingDuration);
        CurrentTrackPosition = TeslaValues.FromMilliseconds(info.NowPlayingElapsed);
    }

    // Tesla reports 0 for a track without a duration.
    private static TimeSpan? Duration(int? milliseconds) => milliseconds is > 0 ? TeslaValues.FromMilliseconds(milliseconds) : null;

    // The snapshot names the source of the player as an enum, which the JSON does not use. The source text of the track details sets Source.
    private void UpdateTrackFromProtobuf(MediaState proto)
    {
        if (proto.OptionalRemoteControlEnabledCase != MediaState.OptionalRemoteControlEnabledOneofCase.None) IsRemoteControlEnabled = proto.RemoteControlEnabled;
        if (proto.OptionalNowPlayingTitleCase != MediaState.OptionalNowPlayingTitleOneofCase.None) CurrentTrackTitle = TeslaValues.Text(proto.NowPlayingTitle);
        if (proto.OptionalNowPlayingArtistCase != MediaState.OptionalNowPlayingArtistOneofCase.None) CurrentTrackArtist = TeslaValues.Text(proto.NowPlayingArtist);
        if (proto.OptionalMediaPlaybackStatusCase != MediaState.OptionalMediaPlaybackStatusOneofCase.None && TeslaProtoValues.MediaPlaybackStatus(proto.MediaPlaybackStatus) is { } status) PlaybackStatus = status;
    }

    private void UpdateVolumeFromProtobuf(MediaState proto)
    {
        if (proto.OptionalAudioVolumeCase != MediaState.OptionalAudioVolumeOneofCase.None) _audioVolume = TeslaValues.Decimal(proto.AudioVolume);
        if (proto.OptionalAudioVolumeMaxCase != MediaState.OptionalAudioVolumeMaxOneofCase.None) _audioVolumeMaximum = TeslaValues.Decimal(proto.AudioVolumeMax) ?? _audioVolumeMaximum;
        UpdateVolume();
    }

    private void UpdateVolume() => Volume = _audioVolumeMaximum is > 0 ? _audioVolume / _audioVolumeMaximum : null;

    private void UpdateDetailsFromProtobuf(MediaDetailState proto)
    {
        if (proto.OptionalNowPlayingSourceStringCase != MediaDetailState.OptionalNowPlayingSourceStringOneofCase.None) Source = TeslaValues.Text(proto.NowPlayingSourceString);
        if (proto.OptionalNowPlayingAlbumCase != MediaDetailState.OptionalNowPlayingAlbumOneofCase.None) CurrentTrackAlbum = TeslaValues.Text(proto.NowPlayingAlbum);
        if (proto.OptionalNowPlayingStationCase != MediaDetailState.OptionalNowPlayingStationOneofCase.None) Station = TeslaValues.Text(proto.NowPlayingStation);
        if (proto.OptionalNowPlayingDurationCase != MediaDetailState.OptionalNowPlayingDurationOneofCase.None) CurrentTrackDuration = Duration(proto.NowPlayingDuration);
        if (proto.OptionalNowPlayingElapsedCase != MediaDetailState.OptionalNowPlayingElapsedOneofCase.None) CurrentTrackPosition = TeslaValues.FromMilliseconds(proto.NowPlayingElapsed);
    }
}
