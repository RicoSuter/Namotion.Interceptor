using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Abstractions.Media;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A visible Sonos room player.
/// </summary>
[InterceptorSubject]
[System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1200", Justification = "A device root aggregates its function subjects and the capability interfaces it implements; splitting it would only spread the same dependencies across files.")]
public partial class SonosPlayer : SonosDevice,
    IAudioPlayer,
    IBatteryState
{
    private readonly SonosSystem _system;

    // Guards the event versus poll ordering: an order and the fields it protects change together. Every poll goes
    // through all three, so a poll superseded by a later one applies nothing; the sleep timer has no events.
    private readonly Lock _stateLock = new();
    private PollEventOrder _avTransportOrder = new();
    private PollEventOrder _renderingControlOrder = new();
    private PollEventOrder _sleepTimerOrder = new();

    internal SonosPlayer(SonosSystem system, string uuid)
        : base(uuid)
    {
        _system = system;
        HomeTheater = null;
        Satellites = new Dictionary<string, SonosSatellite>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The media the player was told to play (AVTransportURI), which identifies the source better than the track.
    /// </summary>
    internal partial string? MediaUri { get; set; }

    public partial bool? IsMuted { get; internal set; }

    public partial decimal? Volume { get; internal set; }

    // Playback, track, source, play mode and sleep timer are group state. A member reports its own play mode and
    // sleep timer and an x-rincon: transport that points at the coordinator, without track details, so the public
    // properties read the coordinator's reported values. They read its raw values rather than its derived ones, so
    // an inconsistent topology cannot make two players recurse.

    internal partial MediaPlaybackState? ReportedPlaybackState { get; set; }

    internal partial string? ReportedTrackTitle { get; set; }

    internal partial string? ReportedTrackArtist { get; set; }

    internal partial string? ReportedTrackAlbum { get; set; }

    internal partial string? ReportedTrackImageUri { get; set; }

    internal partial string? ReportedTrackUri { get; set; }

    internal partial TimeSpan? ReportedTrackPosition { get; set; }

    internal partial TimeSpan? ReportedTrackDuration { get; set; }

    internal partial bool? ReportedShuffle { get; set; }

    internal partial SonosRepeatMode? ReportedRepeat { get; set; }

    internal partial TimeSpan? ReportedSleepTimerRemaining { get; set; }

    internal partial string? ReportedSourceTitle { get; set; }

    internal partial SonosSource? ReportedSource { get; set; }

    /// <summary>
    /// The playback state of the player's group, or null while it is unknown: before the first poll and while the
    /// player or its coordinator is not connected.
    /// </summary>
    [Derived]
    public MediaPlaybackState? PlaybackState
    {
        get
        {
            // The last reported state of an offline speaker says nothing about now, unlike its last track.
            var coordinator = GetCoordinator();
            return IsConnected && coordinator.IsConnected ? coordinator.ReportedPlaybackState : null;
        }
    }

    /// <summary>
    /// Whether the player's group plays or buffers, or null while <see cref="PlaybackState"/> is unknown.
    /// </summary>
    [Derived]
    public bool? IsPlaying => PlaybackState switch
    {
        null => null,
        MediaPlaybackState.Playing or MediaPlaybackState.Buffering => true,
        _ => false
    };

    [Derived]
    public string? CurrentTrackTitle => GetCoordinator().ReportedTrackTitle;

    [Derived]
    public string? CurrentTrackArtist => GetCoordinator().ReportedTrackArtist;

    [Derived]
    public string? CurrentTrackAlbum => GetCoordinator().ReportedTrackAlbum;

    /// <summary>
    /// The album art, resolved against the address of the coordinator, which serves it.
    /// </summary>
    [Derived]
    public string? CurrentTrackImageUri => GetCoordinator().ReportedTrackImageUri;

    [Derived]
    public string? CurrentTrackUri => GetCoordinator().ReportedTrackUri;

    [Derived]
    public TimeSpan? CurrentTrackPosition => GetCoordinator().ReportedTrackPosition;

    [Derived]
    public TimeSpan? CurrentTrackDuration => GetCoordinator().ReportedTrackDuration;

    /// <summary>
    /// Where the audio of the group comes from, or null until the transport of its coordinator was read.
    /// </summary>
    [Derived]
    [State(Position = 11)]
    public SonosSource? Source => GetCoordinator().ReportedSource;

    [Derived]
    [State(Position = 12)]
    public bool? Shuffle => GetCoordinator().ReportedShuffle;

    [Derived]
    [State(Position = 13)]
    public SonosRepeatMode? Repeat => GetCoordinator().ReportedRepeat;

    [Derived]
    [State(Position = 14)]
    public TimeSpan? SleepTimerRemaining => GetCoordinator().ReportedSleepTimerRemaining;

    /// <summary>
    /// The name of what the group plays, such as the radio station or playlist, when Sonos reports it.
    /// </summary>
    [Derived]
    [State(Position = 15)]
    public string? SourceTitle => GetCoordinator().ReportedSourceTitle;

    [State(Position = 20)]
    public partial int? Bass { get; internal set; }

    [State(Position = 21)]
    public partial int? Treble { get; internal set; }

    [State(Position = 22)]
    public partial bool? Loudness { get; internal set; }

    /// <summary>
    /// The home theater settings, or null when the device description does not list <c>HTControl</c>. Created when
    /// the description is first read and then kept, so a poll never replaces it.
    /// </summary>
    [State(Position = 23)]
    public partial SonosHomeTheater? HomeTheater { get; internal set; }

    [State(Position = 30)]
    public partial string? GroupCoordinatorUuid { get; internal set; }

    [Derived]
    [State(Position = 31)]
    public bool IsGroupCoordinator => GroupCoordinatorUuid is null || GroupCoordinatorUuid == Uuid;

    /// <summary>
    /// The key of the player's group in <see cref="SonosSystem.Groups"/>: its coordinator's RINCON id.
    /// </summary>
    internal string GroupKey => GroupCoordinatorUuid ?? Uuid;

    [Derived]
    [State(Position = 32)]
    public bool IsHomeTheater => HomeTheater is not null;

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

    // The coordinator of the player's group, or the player itself when it coordinates or the coordinator is unknown.
    private SonosPlayer GetCoordinator() =>
        _system.Players.GetValueOrDefault(GroupKey) ?? this;

    internal override void ApplyDescription(SonosDeviceDescription description)
    {
        base.ApplyDescription(description);

        // Under the state lock, which the events and polls that update the child hold: concurrent polls of one
        // player must not create two.
        lock (_stateLock)
        {
            var isHomeTheater = ServiceIds.Contains("HTControl");
            if (isHomeTheater != (HomeTheater is not null))
            {
                HomeTheater = isHomeTheater ? new SonosHomeTheater(this) : null;
            }
        }
    }

    internal override void ForgetStateOfOutage()
    {
        lock (_stateLock)
        {
            // A read the speaker answers with a fault keeps the current value, which after an outage would be the
            // one from before it. An answer in between made the player reachable again and its state current.
            if (!IsReachable)
            {
                ReportedPlaybackState = null;
            }
        }
    }

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

            // A satellite's own zone name is not always its room: subwoofers report "Sub".
            satellite.Role = satelliteTopology.Role;
            satellite.ApplyTopology(topology.RoomName, satelliteTopology.BaseUri, satelliteTopology.SoftwareVersion, satelliteTopology.IsWireless);
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

    /// <param name="change">The change the event carries.</param>
    /// <param name="order">When the event arrived, from <see cref="SonosSystem.NextOrder"/>.</param>
    internal void ApplyAvTransportEvent(AvTransportChange change, long order)
    {
        lock (_stateLock)
        {
            _avTransportOrder.RecordEvent(order);
            ApplyAvTransport(change, isPoll: false);
        }
    }

    /// <param name="change">The change the event carries.</param>
    /// <param name="order">When the event arrived, from <see cref="SonosSystem.NextOrder"/>.</param>
    internal void ApplyRenderingControlEvent(RenderingControlChange change, long order)
    {
        lock (_stateLock)
        {
            _renderingControlOrder.RecordEvent(order);
            ApplyRenderingControl(change);
        }
    }

    /// <param name="reading">The values the poll read.</param>
    /// <param name="pollStartedAt">When the poll started, from <see cref="SonosSystem.NextOrder"/>.</param>
    internal void ApplyPoll(SonosPlayerReading reading, long pollStartedAt)
    {
        lock (_stateLock)
        {
            // Command refreshes poll outside the reconciliation, so an older poll can complete after a newer one. A
            // poll that started before the latest event read state the event has since replaced. That includes the
            // position, which belongs to the track the poll read.
            var appliesAvTransport = _avTransportOrder.TryApplyPoll(pollStartedAt);
            var appliesRenderingControl = _renderingControlOrder.TryApplyPoll(pollStartedAt);
            var appliesSleepTimer = _sleepTimerOrder.TryApplyPoll(pollStartedAt);

            if (appliesAvTransport)
            {
                ApplyAvTransport(reading.AvTransport, isPoll: true);
                if (reading.HasPosition)
                {
                    ReportedTrackPosition = reading.Position;
                }
            }

            if (appliesRenderingControl)
            {
                ApplyRenderingControl(reading.RenderingControl);
            }

            if (appliesSleepTimer && reading.HasSleepTimer)
            {
                ReportedSleepTimerRemaining = reading.SleepTimerRemaining;
            }
        }
    }

    private void ApplyAvTransport(AvTransportChange change, bool isPoll)
    {
        if (SonosValues.IsKnown(change.TransportState))
        {
            ReportedPlaybackState = SonosValues.ParsePlaybackState(change.TransportState);
        }

        if (SonosValues.ParsePlayMode(change.PlayMode) is { } playMode)
        {
            ReportedShuffle = playMode.Shuffle;
            ReportedRepeat = playMode.Repeat;
        }

        var (isMediaChange, isTrackChange) = ApplyUris(change);
        if (isTrackChange)
        {
            // Position comes only from polls; the next one reads it for the new track.
            ReportedTrackPosition = null;
        }

        ApplySourceTitle(change.MediaMetaData, isMediaChange);

        if (SonosValues.IsKnown(change.TrackDuration))
        {
            // Streams report a zero duration.
            ReportedTrackDuration = SonosValues.ParseDuration(change.TrackDuration) is { Ticks: > 0 } duration ? duration : null;
        }
        else if (isTrackChange)
        {
            ReportedTrackDuration = null;
        }

        ApplyTrackMetaData(change.TrackMetaData, isTrackChange, isPoll);
    }

    // Caller holds _stateLock. Returns whether the media and the track changed. An unknown value keeps the current
    // one only while the track stays the same: Spotify Connect polls report NOT_IMPLEMENTED for what its events
    // delivered. After a change, the kept values would describe the previous track.
    private (bool IsMediaChange, bool IsTrackChange) ApplyUris(AvTransportChange change)
    {
        var isMediaChange = false;
        var isMediaKnown = SonosValues.IsKnown(change.MediaUri);
        if (isMediaKnown)
        {
            var mediaUri = SonosValues.NullIfEmpty(change.MediaUri);
            isMediaChange = mediaUri != MediaUri;
            MediaUri = mediaUri;
        }

        var isTrackChange = isMediaChange;
        var isTrackKnown = SonosValues.IsKnown(change.TrackUri);
        if (isTrackKnown)
        {
            var trackUri = SonosValues.NullIfEmpty(change.TrackUri);
            isTrackChange |= trackUri != ReportedTrackUri;
            ReportedTrackUri = trackUri;
        }

        if (isMediaKnown || isTrackKnown)
        {
            ReportedSource = SonosUris.DetectSource(MediaUri ?? ReportedTrackUri);
        }

        return (isMediaChange, isTrackChange);
    }

    private void ApplyRenderingControl(RenderingControlChange change)
    {
        if (change.Volume is { } volume)
        {
            Volume = SonosValues.ToFraction(volume);
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

        HomeTheater?.Apply(change);
    }
}
