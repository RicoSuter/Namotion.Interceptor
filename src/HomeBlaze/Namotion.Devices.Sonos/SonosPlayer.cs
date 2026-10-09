using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Abstractions.Media;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A visible Sonos room player.
/// </summary>
[InterceptorSubject]
public partial class SonosPlayer : SonosDevice,
    IAudioPlayer,
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
    public bool LeaveGroup_IsEnabled => CanControl && _system.Groups.GetValueOrDefault(GroupCoordinatorUuid ?? Uuid)?.Members.Length > 1;

    [Operation(Position = 1)]
    public Task PlayAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.PlayAsync(token), cancellationToken);

    [Operation(Position = 2)]
    public Task PauseAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.PauseAsync(token), cancellationToken);

    [Operation(Position = 3)]
    public Task StopAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.StopAsync(token), cancellationToken);

    [Operation(Position = 4)]
    public Task NextAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.NextAsync(token), cancellationToken);

    [Operation(Position = 5)]
    public Task PreviousAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.PreviousAsync(token), cancellationToken);

    [Operation(Position = 6)]
    public Task TogglePlaybackAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.TogglePlaybackAsync(token), cancellationToken);

    [Operation(Position = 7)]
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        return RunOnCoordinatorAsync((connection, token) => connection.SeekAsync(position, token), cancellationToken);
    }

    [Operation(Position = 10)]
    public Task SetVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal volume, CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.SetVolumeAsync(SonosValues.ToSonosVolume(volume), token), cancellationToken);

    [Operation(Position = 11)]
    public Task ChangeVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal delta, CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.ChangeVolumeAsync(SonosValues.ToSonosVolumeAdjustment(delta), token), cancellationToken);

    [Operation(Position = 12)]
    public Task RampVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal volume, CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.RampVolumeAsync(SonosValues.ToSonosVolume(volume), token), cancellationToken);

    [Operation(Position = 13)]
    public Task MuteAsync(CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.SetMuteAsync(true, token), cancellationToken);

    [Operation(Position = 14)]
    public Task UnmuteAsync(CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.SetMuteAsync(false, token), cancellationToken);

    [Operation(Position = 20)]
    public Task PlayFavoriteAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var favorite = _system.FindFavorite(name)
            ?? throw new ArgumentException(
                $"Unknown Sonos favorite '{name}'. Known favorites: {string.Join(", ", _system.Favorites)}.", nameof(name));

        return RunOnCoordinatorAsync(async (connection, token) =>
        {
            if (favorite.IsContainer)
            {
                await connection.PlayFromQueueAsync(favorite.Uri, favorite.Metadata, token);
            }
            else
            {
                await connection.SetTransportUriAsync(favorite.Uri, favorite.Metadata, token);
                await connection.PlayAsync(token);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Plays a URI: http(s) streams and x-rincon-mp3radio URIs play as radio with the optional title; other native
    /// Sonos URIs pass through without metadata.
    /// </summary>
    [Operation(Position = 21)]
    public Task PlayUriAsync(string uri, string? title, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        var isStream = uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                       uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var transportUri = isStream ? SonosValues.ToStreamUri(uri) : uri;
        var isRadio = isStream || uri.StartsWith("x-rincon-mp3radio:", StringComparison.OrdinalIgnoreCase);
        var metadata = isRadio ? SonosValues.CreateStreamMetadata(title ?? uri) : string.Empty;

        return RunOnCoordinatorAsync(async (connection, token) =>
        {
            await connection.SetTransportUriAsync(transportUri, metadata, token);
            await connection.PlayAsync(token);
        }, cancellationToken);
    }

    /// <summary>
    /// Plays a sound over the current playback, which resumes afterwards. Needs S2 speakers.
    /// </summary>
    [Operation(Position = 22)]
    public Task PlayNotificationAsync(string soundUri, [OperationParameter(Unit = StateUnit.Percent)] decimal volume, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(soundUri);
        if (!Uri.TryCreate(soundUri, UriKind.Absolute, out var sound) || (sound.Scheme != Uri.UriSchemeHttp && sound.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The sound URI must be an absolute http or https URI.", nameof(soundUri));
        }

        // The notification volume must be 1 to 100, so 0 % plays at the lowest volume instead of failing.
        var sonosVolume = Math.Clamp(SonosValues.ToSonosVolume(volume), 1, 100);
        return RunOnPlayerAsync((connection, token) => connection.PlayNotificationAsync(sound, sonosVolume, token), cancellationToken);
    }

    [Operation(Position = 23)]
    public Task SwitchToTvAsync(CancellationToken cancellationToken)
    {
        EnsureHomeTheater();
        return RunOnPlayerAsync((connection, token) => connection.SwitchToTvAsync(token), cancellationToken);
    }

    [Operation(Position = 24)]
    public Task SwitchToLineInAsync(CancellationToken cancellationToken)
    {
        if (!HasLineIn)
        {
            throw new InvalidOperationException($"{Title} has no line-in.");
        }

        return RunOnPlayerAsync((connection, token) => connection.SwitchToLineInAsync(token), cancellationToken);
    }

    [Operation(Position = 30)]
    public Task SetShuffleAsync(bool shuffle, CancellationToken cancellationToken)
    {
        var playMode = SonosValues.FormatPlayMode(shuffle, GetCoordinator().Repeat ?? SonosRepeatMode.Off);
        return RunOnCoordinatorAsync((connection, token) => connection.SetPlayModeAsync(playMode, token), cancellationToken);
    }

    [Operation(Position = 31)]
    public Task SetRepeatAsync(SonosRepeatMode repeat, CancellationToken cancellationToken)
    {
        var playMode = SonosValues.FormatPlayMode(GetCoordinator().Shuffle ?? false, repeat);
        return RunOnCoordinatorAsync((connection, token) => connection.SetPlayModeAsync(playMode, token), cancellationToken);
    }

    /// <summary>
    /// Sets the sleep timer; zero cancels it.
    /// </summary>
    [Operation(Position = 32)]
    public Task SetSleepTimerAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        return RunOnCoordinatorAsync((connection, token) => connection.SetSleepTimerAsync(duration, token), cancellationToken);
    }

    [Operation(Position = 40)]
    public Task SetBassAsync(int bass, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bass, -10);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bass, 10);
        return RunOnPlayerAsync((connection, token) => connection.SetBassAsync(bass, token), cancellationToken);
    }

    [Operation(Position = 41)]
    public Task SetTrebleAsync(int treble, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(treble, -10);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(treble, 10);
        return RunOnPlayerAsync((connection, token) => connection.SetTrebleAsync(treble, token), cancellationToken);
    }

    [Operation(Position = 42)]
    public Task SetLoudnessAsync(bool loudness, CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.SetLoudnessAsync(loudness, token), cancellationToken);

    [Operation(Position = 43)]
    public Task SetNightModeAsync(bool nightMode, CancellationToken cancellationToken)
    {
        EnsureHomeTheater();
        return RunOnPlayerAsync((connection, token) => connection.SetEqualizerAsync("NightMode", nightMode, token), cancellationToken);
    }

    [Operation(Position = 44)]
    public Task SetSpeechEnhancementAsync(bool speechEnhancement, CancellationToken cancellationToken)
    {
        EnsureHomeTheater();
        return RunOnPlayerAsync((connection, token) => connection.SetEqualizerAsync("DialogLevel", speechEnhancement, token), cancellationToken);
    }

    [Operation(Position = 50)]
    public Task JoinGroupAsync(string roomNameOrUuid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roomNameOrUuid);
        var target = _system.FindPlayer(roomNameOrUuid)
            ?? throw _system.CreateUnknownRoomException(roomNameOrUuid, nameof(roomNameOrUuid));
        if (ReferenceEquals(target, this))
        {
            throw new ArgumentException("A player cannot join its own group.", nameof(roomNameOrUuid));
        }

        var coordinatorUuid = target.GroupCoordinatorUuid ?? target.Uuid;
        if ((GroupCoordinatorUuid ?? Uuid) == coordinatorUuid)
        {
            return Task.CompletedTask;
        }

        return RunGroupingOnPlayerAsync((connection, token) => connection.JoinAsync(coordinatorUuid, token), cancellationToken);
    }

    [Operation(Position = 51)]
    public Task LeaveGroupAsync(CancellationToken cancellationToken) =>
        RunGroupingOnPlayerAsync((connection, token) => connection.LeaveGroupAsync(token), cancellationToken);

    private void EnsureHomeTheater()
    {
        if (!IsHomeTheater)
        {
            throw new InvalidOperationException($"{Title} is not a home theater player.");
        }
    }

    // Play mode is group state, so it is read from the coordinator subject.
    private SonosPlayer GetCoordinator() =>
        _system.Players.GetValueOrDefault(GroupCoordinatorUuid ?? Uuid) ?? this;

    private Task RunOnCoordinatorAsync(Func<SonosConnection, CancellationToken, Task> command, CancellationToken cancellationToken) =>
        RunAsync(GroupCoordinatorUuid ?? Uuid, command, cancellationToken);

    private Task RunOnPlayerAsync(Func<SonosConnection, CancellationToken, Task> command, CancellationToken cancellationToken) =>
        RunAsync(Uuid, command, cancellationToken);

    private async Task RunGroupingOnPlayerAsync(Func<SonosConnection, CancellationToken, Task> command, CancellationToken cancellationToken)
    {
        var connection = _system.GetConnectionForCommand(Uuid);
        await _system.RunGroupingCommandsAsync(token => command(connection, token), cancellationToken);
    }

    private async Task RunAsync(string targetUuid, Func<SonosConnection, CancellationToken, Task> command, CancellationToken cancellationToken)
    {
        var connection = _system.GetConnectionForCommand(targetUuid);
        try
        {
            await command(connection, cancellationToken);
        }
        finally
        {
            // Also after a failure: a multi-step command may have partly applied. The refresh logs its own failures.
            await _system.RefreshAfterCommandAsync(this, cancellationToken);
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
