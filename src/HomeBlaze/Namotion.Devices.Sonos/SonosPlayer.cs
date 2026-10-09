using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Abstractions.Media;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A visible Sonos room player.
/// </summary>
[InterceptorSubject]
public partial class SonosPlayer : SonosDevice,
    IAudioPlayerState,
    IMediaTrackState,
    IBatteryState
{
    private readonly SonosSystem _system;

    // Guards the event versus poll ordering: a timestamp and the fields it protects change together.
    private readonly Lock _stateLock = new();
    private DateTimeOffset _lastAvTransportEventAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRenderingControlEventAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPollStartedAt = DateTimeOffset.MinValue;

    // Every event and poll repeats the metadata, so the last parse is reused while the raw string is unchanged.
    private string? _lastTrackMetaData;
    private DidlTrack? _lastTrack;

    internal SonosPlayer(SonosSystem system, string uuid)
        : base(uuid)
    {
        _system = system;
        TransportState = SonosTransportState.Unknown;
        Satellites = new Dictionary<string, SonosSatellite>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The media the player was told to play (AVTransportURI), which identifies the source better than the track.
    /// </summary>
    internal partial string? MediaUri { get; set; }

    [State(Position = 10)]
    public partial SonosTransportState TransportState { get; internal set; }

    [Derived]
    public bool? IsPlaying => TransportState switch
    {
        SonosTransportState.Playing or SonosTransportState.Transitioning => true,
        SonosTransportState.Unknown => null,
        _ => false
    };

    public partial bool? IsMuted { get; internal set; }

    public partial decimal? Volume { get; internal set; }

    public partial string? CurrentTrackTitle { get; internal set; }

    public partial string? CurrentTrackArtist { get; internal set; }

    public partial string? CurrentTrackAlbum { get; internal set; }

    public partial string? CurrentTrackImageUri { get; internal set; }

    public partial string? CurrentTrackUri { get; internal set; }

    public partial TimeSpan? CurrentTrackPosition { get; internal set; }

    public partial TimeSpan? CurrentTrackDuration { get; internal set; }

    [Derived]
    [State(Position = 11)]
    public SonosSource Source => SonosValues.DetectSource(MediaUri ?? CurrentTrackUri);

    [State(Position = 12)]
    public partial bool? Shuffle { get; internal set; }

    [State(Position = 13)]
    public partial SonosRepeatMode? Repeat { get; internal set; }

    [State(Position = 14)]
    public partial TimeSpan? SleepTimerRemaining { get; internal set; }

    [State(Position = 20)]
    public partial int? Bass { get; internal set; }

    [State(Position = 21)]
    public partial int? Treble { get; internal set; }

    [State(Position = 22)]
    public partial bool? Loudness { get; internal set; }

    [State(Position = 23)]
    public partial bool? NightMode { get; internal set; }

    [State(Position = 24)]
    public partial bool? SpeechEnhancement { get; internal set; }

    [State(Position = 30)]
    public partial string? GroupCoordinatorUuid { get; internal set; }

    [Derived]
    [State(Position = 31)]
    public bool IsGroupCoordinator => GroupCoordinatorUuid is null || GroupCoordinatorUuid == Uuid;

    [Derived]
    [State(Position = 32)]
    public bool IsHomeTheater => ServiceIds.Contains("HTControl");

    [Derived]
    [State(Position = 33)]
    public bool HasLineIn => ServiceIds.Contains("AudioIn");

    public partial decimal? BatteryLevel { get; internal set; }

    [State(Position = 341)]
    public partial bool? IsCharging { get; internal set; }

    [State(Position = 40)]
    public partial Dictionary<string, SonosSatellite> Satellites { get; internal set; }

    [Derived]
    public override string? IconName => IsPlaying == true ? "PlayCircle" : "Speaker";

    // Operation availability. The attribute names match the operation method names without "Async".

    private bool CanControl => IsConnected && _system.IsConnected;

    [Derived]
    [PropertyAttribute("Play", KnownAttributes.IsEnabled)]
    public bool Play_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Pause", KnownAttributes.IsEnabled)]
    public bool Pause_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Stop", KnownAttributes.IsEnabled)]
    public bool Stop_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Next", KnownAttributes.IsEnabled)]
    public bool Next_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Previous", KnownAttributes.IsEnabled)]
    public bool Previous_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("TogglePlayback", KnownAttributes.IsEnabled)]
    public bool TogglePlayback_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Seek", KnownAttributes.IsEnabled)]
    public bool Seek_IsEnabled => CanControl && CurrentTrackDuration > TimeSpan.Zero;

    [Derived]
    [PropertyAttribute("SwitchToTv", KnownAttributes.IsEnabled)]
    public bool SwitchToTv_IsEnabled => CanControl && IsHomeTheater;

    [Derived]
    [PropertyAttribute("SwitchToLineIn", KnownAttributes.IsEnabled)]
    public bool SwitchToLineIn_IsEnabled => CanControl && HasLineIn;

    [Derived]
    [PropertyAttribute("SetNightMode", KnownAttributes.IsEnabled)]
    public bool SetNightMode_IsEnabled => CanControl && IsHomeTheater;

    [Derived]
    [PropertyAttribute("SetSpeechEnhancement", KnownAttributes.IsEnabled)]
    public bool SetSpeechEnhancement_IsEnabled => CanControl && IsHomeTheater;

    [Derived]
    [PropertyAttribute("LeaveGroup", KnownAttributes.IsEnabled)]
    public bool LeaveGroup_IsEnabled => CanControl && !IsGroupCoordinator;

    internal void ApplyPlayerTopology(SonosTopologyPlayer topology, string coordinatorUuid)
    {
        ApplyTopology(topology.RoomName, topology.BaseUri, topology.SoftwareVersion, topology.IsWireless);
        GroupCoordinatorUuid = coordinatorUuid;

        var (batteryLevel, isCharging) = SonosValues.ParseBattery(topology.MoreInfo);
        BatteryLevel = batteryLevel;
        IsCharging = isCharging;

        var satellites = Satellites;
        Dictionary<string, SonosSatellite>? updatedSatellites = null;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var satelliteTopology in topology.Satellites)
        {
            present.Add(satelliteTopology.Uuid);
            if (!satellites.TryGetValue(satelliteTopology.Uuid, out var satellite))
            {
                satellite = new SonosSatellite(satelliteTopology.Uuid);
                updatedSatellites ??= new Dictionary<string, SonosSatellite>(satellites, StringComparer.Ordinal);
                updatedSatellites[satelliteTopology.Uuid] = satellite;
            }

            satellite.Role = satelliteTopology.Role;
            satellite.ApplyTopology(satelliteTopology.RoomName, satelliteTopology.BaseUri, satelliteTopology.SoftwareVersion, satelliteTopology.IsWireless);
        }

        foreach (var (uuid, satellite) in satellites)
        {
            if (!present.Contains(uuid))
            {
                satellite.MarkMissing();
            }
        }

        if (updatedSatellites is not null)
        {
            Satellites = updatedSatellites;
        }
    }

    internal void ApplyAvTransportEvent(AvTransportChange change, DateTimeOffset receivedAt)
    {
        lock (_stateLock)
        {
            _lastAvTransportEventAt = receivedAt;
            ApplyAvTransport(change);
        }
    }

    internal void ApplyRenderingControlEvent(RenderingControlChange change, DateTimeOffset receivedAt)
    {
        lock (_stateLock)
        {
            _lastRenderingControlEventAt = receivedAt;
            ApplyRenderingControl(change);
        }
    }

    internal void ApplyPoll(SonosPlayerReading reading, DateTimeOffset pollStartedAt)
    {
        lock (_stateLock)
        {
            // Command refreshes poll outside the reconciliation, so an older poll can complete after a newer one.
            if (SonosValues.IsSupersededPoll(pollStartedAt, _lastPollStartedAt))
            {
                return;
            }

            _lastPollStartedAt = pollStartedAt;

            // A poll that started before the latest event read state the event has since replaced.
            if (_lastAvTransportEventAt <= pollStartedAt)
            {
                ApplyAvTransport(reading.AvTransport);
            }

            if (_lastRenderingControlEventAt <= pollStartedAt)
            {
                ApplyRenderingControl(reading.RenderingControl);
            }

            CurrentTrackPosition = reading.Position;
            SleepTimerRemaining = reading.SleepTimerRemaining;
        }
    }

    private void ApplyAvTransport(AvTransportChange change)
    {
        if (SonosValues.IsKnown(change.TransportState))
        {
            TransportState = SonosValues.ParseTransportState(change.TransportState);
        }

        if (SonosValues.ParsePlayMode(change.PlayMode) is { } playMode)
        {
            Shuffle = playMode.Shuffle;
            Repeat = playMode.Repeat;
        }

        if (SonosValues.IsKnown(change.MediaUri))
        {
            MediaUri = SonosValues.NullIfEmpty(change.MediaUri);
        }

        if (SonosValues.IsKnown(change.TrackUri))
        {
            CurrentTrackUri = SonosValues.NullIfEmpty(change.TrackUri);
        }

        if (SonosValues.IsKnown(change.TrackDuration))
        {
            CurrentTrackDuration = SonosValues.ParseDuration(change.TrackDuration);
        }

        if (SonosValues.IsKnown(change.TrackMetaData))
        {
            if (change.TrackMetaData != _lastTrackMetaData)
            {
                _lastTrack = DidlParser.ParseTrack(change.TrackMetaData);
                _lastTrackMetaData = change.TrackMetaData;
            }

            var track = _lastTrack;
            CurrentTrackTitle = track?.Title;
            CurrentTrackArtist = track?.Artist;
            CurrentTrackAlbum = track?.Album;
            CurrentTrackImageUri = SonosValues.ToAbsoluteUri(track?.AlbumArtUri, BaseUri);
        }
    }

    private void ApplyRenderingControl(RenderingControlChange change)
    {
        if (change.Volume is { } volume)
        {
            Volume = SonosValues.ToVolume(volume);
        }

        if (change.Mute is { } mute)
        {
            IsMuted = mute;
        }

        if (change.Bass is { } bass)
        {
            Bass = bass;
        }

        if (change.Treble is { } treble)
        {
            Treble = treble;
        }

        if (change.Loudness is { } loudness)
        {
            Loudness = loudness;
        }

        if (change.NightMode is { } nightMode)
        {
            NightMode = nightMode;
        }

        if (change.SpeechEnhancement is { } speechEnhancement)
        {
            SpeechEnhancement = speechEnhancement;
        }
    }
}
