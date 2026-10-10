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
[System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1200", Justification = "A device root aggregates its function subjects and the capability interfaces it implements; splitting it would only spread the same dependencies across files.")]
public partial class SonosPlayer : SonosDevice,
    IAudioPlayer,
    IMediaTrackState,
    IBatteryState
{
    private readonly SonosSystem _system;

    // Guards the event versus poll ordering: an order and the fields it protects change together. Every poll goes
    // through all three, so a poll superseded by a later one applies nothing; the sleep timer has no events.
    private readonly Lock _stateLock = new();
    private PollEventOrder _avTransportOrder = new();
    private PollEventOrder _renderingControlOrder = new();
    private PollEventOrder _sleepTimerOrder = new();

    // Every event and poll repeats the metadata, so the last parse is reused while the raw string is unchanged, and
    // the album art URI while the track and the speaker address are. Guarded by _stateLock.
    private string? _lastTrackMetaData;
    private DidlTrack? _lastTrack;
    private string? _lastMediaMetaData;
    private string? _lastMediaTitle;
    private DidlTrack? _imageUriTrack;
    private Uri? _imageUriBaseUri;
    private string? _imageUri;
    private DidlTrack? _titleTrack;
    private string? _titleTrackUri;
    private string? _title;

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

    // Source, play mode and sleep timer are group state. A member reports its own play mode and sleep timer and an
    // x-rincon: transport that points at the coordinator, so these read the coordinator's reported values. They read
    // its raw values rather than its derived ones, so an inconsistent topology cannot make two players recurse.

    internal partial bool? ReportedShuffle { get; set; }

    internal partial SonosRepeatMode? ReportedRepeat { get; set; }

    internal partial TimeSpan? ReportedSleepTimerRemaining { get; set; }

    internal partial string? ReportedMediaTitle { get; set; }

    [Derived]
    [State(Position = 11)]
    public SonosSource Source
    {
        get
        {
            var coordinator = GetCoordinator();
            return SonosValues.DetectSource(coordinator.MediaUri ?? coordinator.CurrentTrackUri);
        }
    }

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
    public string? MediaTitle => GetCoordinator().ReportedMediaTitle;

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

    /// <summary>
    /// The key of the player's group in <see cref="SonosSystem.Groups"/>: its coordinator's RINCON id.
    /// </summary>
    internal string GroupKey => GroupCoordinatorUuid ?? Uuid;

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

    // Commands for group playback go to the coordinator, so it must be reachable as well.
    private bool CanControlCoordinator => CanControl && GetCoordinator().IsConnected;

    [Derived]
    [PropertyAttribute("Play", KnownAttributes.IsEnabled)]
    public bool Play_IsEnabled => CanControlCoordinator;

    [Derived]
    [PropertyAttribute("Pause", KnownAttributes.IsEnabled)]
    public bool Pause_IsEnabled => CanControlCoordinator;

    [Derived]
    [PropertyAttribute("Stop", KnownAttributes.IsEnabled)]
    public bool Stop_IsEnabled => CanControlCoordinator;

    [Derived]
    [PropertyAttribute("Next", KnownAttributes.IsEnabled)]
    public bool Next_IsEnabled => CanControlCoordinator;

    [Derived]
    [PropertyAttribute("Previous", KnownAttributes.IsEnabled)]
    public bool Previous_IsEnabled => CanControlCoordinator;

    [Derived]
    [PropertyAttribute("TogglePlayback", KnownAttributes.IsEnabled)]
    public bool TogglePlayback_IsEnabled => CanControlCoordinator;

    [Derived]
    [PropertyAttribute("Seek", KnownAttributes.IsEnabled)]
    public bool Seek_IsEnabled => CanControlCoordinator && CurrentTrackDuration > TimeSpan.Zero;

    [Derived]
    [PropertyAttribute("SetVolume", KnownAttributes.IsEnabled)]
    public bool SetVolume_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("ChangeVolume", KnownAttributes.IsEnabled)]
    public bool ChangeVolume_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("RampVolume", KnownAttributes.IsEnabled)]
    public bool RampVolume_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Mute", KnownAttributes.IsEnabled)]
    public bool Mute_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Unmute", KnownAttributes.IsEnabled)]
    public bool Unmute_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("PlayFavorite", KnownAttributes.IsEnabled)]
    public bool PlayFavorite_IsEnabled => CanControlCoordinator;

    [Derived]
    [PropertyAttribute("PlayUri", KnownAttributes.IsEnabled)]
    public bool PlayUri_IsEnabled => CanControlCoordinator;

    [Derived]
    [PropertyAttribute("PlayStream", KnownAttributes.IsEnabled)]
    public bool PlayStream_IsEnabled => CanControlCoordinator;

    [Derived]
    [PropertyAttribute("PlayNotification", KnownAttributes.IsEnabled)]
    public bool PlayNotification_IsEnabled => CanControl;

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
    [PropertyAttribute("SetShuffle", KnownAttributes.IsEnabled)]
    public bool SetShuffle_IsEnabled => CanControlCoordinator;

    [Derived]
    [PropertyAttribute("SetRepeat", KnownAttributes.IsEnabled)]
    public bool SetRepeat_IsEnabled => CanControlCoordinator;

    [Derived]
    [PropertyAttribute("SetSleepTimer", KnownAttributes.IsEnabled)]
    public bool SetSleepTimer_IsEnabled => CanControlCoordinator;

    [Derived]
    [PropertyAttribute("SetBass", KnownAttributes.IsEnabled)]
    public bool SetBass_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("SetTreble", KnownAttributes.IsEnabled)]
    public bool SetTreble_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("SetLoudness", KnownAttributes.IsEnabled)]
    public bool SetLoudness_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("JoinGroup", KnownAttributes.IsEnabled)]
    public bool JoinGroup_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("LeaveGroup", KnownAttributes.IsEnabled)]
    public bool LeaveGroup_IsEnabled => CanControl && _system.Groups.GetValueOrDefault(GroupKey)?.Members.Length > 1;

    [Operation(Title = "Play", Icon = "PlayArrow", Position = 1, Description = "Starts or resumes playback of the player's group.")]
    public Task PlayAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.PlayAsync(token), cancellationToken);

    [Operation(Title = "Pause", Icon = "Pause", Position = 2, Description = "Pauses playback of the player's group.")]
    public Task PauseAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.PauseAsync(token), cancellationToken);

    [Operation(Title = "Stop", Icon = "Stop", Position = 3, Description = "Stops playback of the player's group.")]
    public Task StopAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.StopAsync(token), cancellationToken);

    [Operation(Title = "Next", Icon = "SkipNext", Position = 4, Description = "Skips to the next track of the player's group.")]
    public Task NextAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.NextAsync(token), cancellationToken);

    [Operation(Title = "Previous", Icon = "SkipPrevious", Position = 5, Description = "Goes back to the previous track of the player's group.")]
    public Task PreviousAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.PreviousAsync(token), cancellationToken);

    [Operation(Title = "Play or Pause", Position = 6, Description = "Pauses the player's group when it plays, otherwise starts playback.")]
    public Task TogglePlaybackAsync(CancellationToken cancellationToken) =>
        RunOnCoordinatorAsync((connection, token) => connection.TogglePlaybackAsync(token), cancellationToken);

    [Operation(Title = "Seek", Position = 7, Description = "Seeks the current track of the player's group to the given position.")]
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        return RunOnCoordinatorAsync((connection, token) => connection.SeekAsync(position, token), cancellationToken);
    }

    [Operation(Title = "Set Volume", Position = 10, Description = "Sets the volume of this player.")]
    public Task SetVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal volume, CancellationToken cancellationToken)
    {
        SonosValues.ThrowIfFractionOutOfRange(volume, 0m);
        return RunOnPlayerAsync((connection, token) => connection.SetVolumeAsync(SonosValues.ToSonosPercent(volume, 0m), token), cancellationToken);
    }

    [Operation(Title = "Change Volume", Position = 11, Description = "Changes the volume of this player by a relative amount.")]
    public Task ChangeVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal delta, CancellationToken cancellationToken)
    {
        SonosValues.ThrowIfFractionOutOfRange(delta, -1m);
        return RunOnPlayerAsync((connection, token) => connection.ChangeVolumeAsync(SonosValues.ToSonosPercent(delta, -1m), token), cancellationToken);
    }

    [Operation(Title = "Ramp Volume", Position = 12, Description = "Ramps the volume of this player gradually to the given value.")]
    public Task RampVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal volume, CancellationToken cancellationToken)
    {
        SonosValues.ThrowIfFractionOutOfRange(volume, 0m);
        return RunOnPlayerAsync((connection, token) => connection.RampVolumeAsync(SonosValues.ToSonosPercent(volume, 0m), token), cancellationToken);
    }

    [Operation(Title = "Mute", Icon = "VolumeOff", Position = 13, Description = "Mutes this player.")]
    public Task MuteAsync(CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.SetMuteAsync(true, token), cancellationToken);

    [Operation(Title = "Unmute", Icon = "VolumeUp", Position = 14, Description = "Unmutes this player.")]
    public Task UnmuteAsync(CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.SetMuteAsync(false, token), cancellationToken);

    [Operation(Title = "Play Favorite", Position = 20, Description = "Plays the Sonos favorite with the given title on the player's group.")]
    public Task PlayFavoriteAsync(string title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);

        // Before the first connection no favorites are known, which would misreport every title as unknown.
        if (!_system.IsConnected)
        {
            return Task.FromException(_system.CreateNotConnectedException());
        }

        var favorite = _system.FindFavorite(title)
            ?? throw new ArgumentException(
                $"Unknown Sonos favorite '{title}'. Known favorites: {string.Join(", ", _system.Favorites.Select(known => known.Title))}.", nameof(title));

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
    /// Plays a URI once as a normal track, which ends and can be sought. http(s) and native Sonos URIs are sent
    /// unchanged, without metadata.
    /// </summary>
    [Operation(Title = "Play URI", Position = 21, Description = "Plays a URI once as a normal track on the player's group, which ends and can be sought.")]
    public Task PlayUriAsync(string uri, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        return RunOnCoordinatorAsync(async (connection, token) =>
        {
            await connection.SetTransportUriAsync(uri, string.Empty, token);
            await connection.PlayAsync(token);
        }, cancellationToken);
    }

    /// <summary>
    /// Plays a radio or live stream, which Sonos reconnects when it ends. http(s) URIs are played through the
    /// x-rincon-mp3radio scheme; x-rincon-mp3radio URIs are used as they are. Both get the title as metadata, an empty
    /// one when it is null.
    /// </summary>
    /// <exception cref="ArgumentException">The URI uses another scheme.</exception>
    [Operation(Title = "Play Stream", Position = 22, Description = "Plays an http(s) or x-rincon-mp3radio stream on the player's group as radio, which Sonos reconnects when it ends.")]
    public Task PlayStreamAsync(string uri, string? title, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        var isHttp = SonosValues.IsHttpUri(uri);
        if (!isHttp && !uri.StartsWith("x-rincon-mp3radio:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The stream URI must start with http://, https:// or x-rincon-mp3radio:.", nameof(uri));
        }

        var transportUri = isHttp ? SonosValues.ToStreamUri(uri) : uri;
        var metadata = SonosValues.CreateStreamMetadata(title);

        return RunOnCoordinatorAsync(async (connection, token) =>
        {
            await connection.SetTransportUriAsync(transportUri, metadata, token);
            await connection.PlayAsync(token);
        }, cancellationToken);
    }

    /// <summary>
    /// Plays a sound over the current playback, which resumes afterwards. Needs S2 speakers.
    /// </summary>
    [Operation(Title = "Play Notification", Position = 23, Description = "Plays an http(s) sound over the current playback of this player, which resumes afterwards; needs S2 speakers.")]
    public Task PlayNotificationAsync(string soundUri, [OperationParameter(Unit = StateUnit.Percent)] decimal volume, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(soundUri);
        if (!Uri.TryCreate(soundUri, UriKind.Absolute, out var sound) || (sound.Scheme != Uri.UriSchemeHttp && sound.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The sound URI must be an absolute http or https URI.", nameof(soundUri));
        }

        SonosValues.ThrowIfFractionOutOfRange(volume, 0m);

        // The notification volume must be 1 to 100, so 0 % plays at the lowest volume instead of failing.
        var sonosVolume = Math.Clamp(SonosValues.ToSonosPercent(volume, 0m), 1, 100);
        return RunOnPlayerAsync((connection, token) => connection.PlayNotificationAsync(sound, sonosVolume, token), cancellationToken);
    }

    [Operation(Title = "Switch to TV", Position = 24, Description = "Switches this home theater player to its TV input.")]
    public Task SwitchToTvAsync(CancellationToken cancellationToken)
    {
        EnsureHomeTheater();
        return RunOnPlayerAsync((connection, token) => connection.SwitchToTvAsync(token), cancellationToken);
    }

    [Operation(Title = "Switch to Line-In", Position = 25, Description = "Switches this player to its line-in input.")]
    public Task SwitchToLineInAsync(CancellationToken cancellationToken)
    {
        if (!HasLineIn)
        {
            throw new InvalidOperationException($"{Title} has no line-in.");
        }

        return RunOnPlayerAsync((connection, token) => connection.SwitchToLineInAsync(token), cancellationToken);
    }

    [Operation(Title = "Set Shuffle", Position = 30, Description = "Turns shuffle on or off for the player's group.")]
    public Task SetShuffleAsync(bool shuffle, CancellationToken cancellationToken)
    {
        var playMode = SonosValues.FormatPlayMode(shuffle, GetCoordinator().Repeat ?? SonosRepeatMode.Off);
        return RunOnCoordinatorAsync((connection, token) => connection.SetPlayModeAsync(playMode, token), cancellationToken);
    }

    [Operation(Title = "Set Repeat", Position = 31, Description = "Sets the repeat mode of the player's group.")]
    public Task SetRepeatAsync(SonosRepeatMode repeat, CancellationToken cancellationToken)
    {
        var playMode = SonosValues.FormatPlayMode(GetCoordinator().Shuffle ?? false, repeat);
        return RunOnCoordinatorAsync((connection, token) => connection.SetPlayModeAsync(playMode, token), cancellationToken);
    }

    /// <summary>
    /// Sets the sleep timer, at most 23:59:59; zero cancels it.
    /// </summary>
    [Operation(Title = "Set Sleep Timer", Position = 32, Description = "Sets the sleep timer of the player's group, at most 23:59:59; zero cancels it.")]
    public Task SetSleepTimerAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duration, SonosValues.MaximumSleepTimer);
        return RunOnCoordinatorAsync((connection, token) => connection.SetSleepTimerAsync(duration, token), cancellationToken);
    }

    [Operation(Title = "Set Bass", Position = 40, Description = "Sets the bass of this player, from -10 to 10.")]
    public Task SetBassAsync(int bass, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bass, -10);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bass, 10);
        return RunOnPlayerAsync((connection, token) => connection.SetBassAsync(bass, token), cancellationToken);
    }

    [Operation(Title = "Set Treble", Position = 41, Description = "Sets the treble of this player, from -10 to 10.")]
    public Task SetTrebleAsync(int treble, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(treble, -10);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(treble, 10);
        return RunOnPlayerAsync((connection, token) => connection.SetTrebleAsync(treble, token), cancellationToken);
    }

    [Operation(Title = "Set Loudness", Position = 42, Description = "Turns loudness compensation on or off for this player.")]
    public Task SetLoudnessAsync(bool loudness, CancellationToken cancellationToken) =>
        RunOnPlayerAsync((connection, token) => connection.SetLoudnessAsync(loudness, token), cancellationToken);

    [Operation(Title = "Set Night Mode", Position = 43, Description = "Turns night mode on or off for this home theater player.")]
    public Task SetNightModeAsync(bool nightMode, CancellationToken cancellationToken)
    {
        EnsureHomeTheater();
        return RunOnPlayerAsync((connection, token) => connection.SetEqualizerAsync("NightMode", nightMode, token), cancellationToken);
    }

    [Operation(Title = "Set Speech Enhancement", Position = 44, Description = "Turns speech enhancement on or off for this home theater player.")]
    public Task SetSpeechEnhancementAsync(bool speechEnhancement, CancellationToken cancellationToken)
    {
        EnsureHomeTheater();
        return RunOnPlayerAsync((connection, token) => connection.SetEqualizerAsync("DialogLevel", speechEnhancement, token), cancellationToken);
    }

    /// <summary>
    /// Joins this player to the group of another room.
    /// </summary>
    /// <param name="room">The room name or UUID of a player in the group to join.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [Operation(Title = "Join Group", Position = 50, Description = "Joins this player to the group of the given room, by room name or UUID.")]
    public Task JoinGroupAsync(string room, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        var target = _system.FindPlayer(room)
            ?? throw _system.CreateUnknownRoomException(room, nameof(room));
        if (ReferenceEquals(target, this))
        {
            throw new ArgumentException("A player cannot join its own group.", nameof(room));
        }

        var coordinatorUuid = target.GroupKey;
        if (GroupKey == coordinatorUuid)
        {
            return Task.CompletedTask;
        }

        return RunGroupingOnPlayerAsync(
            (connection, token) => connection.JoinAsync(coordinatorUuid, token),
            () => GroupKey == coordinatorUuid,
            cancellationToken);
    }

    [Operation(Title = "Leave Group", Position = 51, Description = "Removes this player from its group so it plays standalone.")]
    public Task LeaveGroupAsync(CancellationToken cancellationToken) =>
        RunGroupingOnPlayerAsync(
            (connection, token) => connection.LeaveGroupAsync(token),
            () => GroupKey == Uuid && !_system.Players.Values.Any(player => player != this && player.IsInTopology && player.GroupKey == Uuid),
            cancellationToken);

    private void EnsureHomeTheater()
    {
        if (!IsHomeTheater)
        {
            throw new InvalidOperationException($"{Title} is not a home theater player.");
        }
    }

    // The coordinator of the player's group, or the player itself when it coordinates or the coordinator is unknown.
    private SonosPlayer GetCoordinator() =>
        _system.Players.GetValueOrDefault(GroupKey) ?? this;

    private Task RunOnCoordinatorAsync(Func<SonosConnection, CancellationToken, Task> command, CancellationToken cancellationToken) =>
        RunAsync(GroupKey, command, cancellationToken);

    /// <summary>
    /// Runs a command through this player's connection, then reads its group back.
    /// </summary>
    internal Task RunOnPlayerAsync(Func<SonosConnection, CancellationToken, Task> command, CancellationToken cancellationToken) =>
        RunAsync(Uuid, command, cancellationToken);

    private async Task RunGroupingOnPlayerAsync(Func<SonosConnection, CancellationToken, Task> command, Func<bool> isApplied, CancellationToken cancellationToken)
    {
        var connection = _system.GetConnectionForCommand(Uuid);
        await _system.RunGroupingCommandsAsync(token => command(connection, token), isApplied, cancellationToken);
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
            ApplyAvTransport(change);
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
                ApplyAvTransport(reading.AvTransport);
                if (reading.HasPosition)
                {
                    CurrentTrackPosition = reading.Position;
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

    private void ApplyAvTransport(AvTransportChange change)
    {
        if (SonosValues.IsKnown(change.TransportState))
        {
            TransportState = SonosValues.ParseTransportState(change.TransportState);
        }

        if (SonosValues.ParsePlayMode(change.PlayMode) is { } playMode)
        {
            ReportedShuffle = playMode.Shuffle;
            ReportedRepeat = playMode.Repeat;
        }

        // An unknown value keeps the current one only while the track stays the same: Spotify Connect polls report
        // NOT_IMPLEMENTED for what its events delivered. After a change, the kept values would describe the
        // previous track.
        var isMediaChange = false;
        if (SonosValues.IsKnown(change.MediaUri))
        {
            var mediaUri = SonosValues.NullIfEmpty(change.MediaUri);
            isMediaChange = mediaUri != MediaUri;
            MediaUri = mediaUri;
        }

        var isTrackChange = isMediaChange;

        if (SonosValues.IsKnown(change.TrackUri))
        {
            var trackUri = SonosValues.NullIfEmpty(change.TrackUri);
            isTrackChange |= trackUri != CurrentTrackUri;
            CurrentTrackUri = trackUri;
        }

        if (isTrackChange)
        {
            // Position comes only from polls; the next one reads it for the new track.
            CurrentTrackPosition = null;
        }

        ApplyMediaTitle(change.MediaMetaData, isMediaChange);

        if (SonosValues.IsKnown(change.TrackDuration))
        {
            // Streams report a zero duration.
            CurrentTrackDuration = SonosValues.ParseDuration(change.TrackDuration) is { Ticks: > 0 } duration ? duration : null;
        }
        else if (isTrackChange)
        {
            CurrentTrackDuration = null;
        }

        ApplyTrackMetaData(change.TrackMetaData, isTrackChange);
    }

    // Caller holds _stateLock. Polls often report the media metadata empty that events delivered, so only a parsable
    // title replaces it. The next track of the same queue or station keeps it.
    private void ApplyMediaTitle(string? mediaMetaData, bool isMediaChange)
    {
        if (mediaMetaData != _lastMediaMetaData || isMediaChange)
        {
            var title = DidlParser.ParseTitle(mediaMetaData);
            _lastMediaTitle = title is not null && SonosValues.IsTitleOfStreamUri(title, MediaUri) ? null : title;
            _lastMediaMetaData = mediaMetaData;
        }

        if (_lastMediaTitle is { } mediaTitle)
        {
            ReportedMediaTitle = mediaTitle;
        }
        else if (isMediaChange)
        {
            ReportedMediaTitle = null;
        }
    }

    // Caller holds _stateLock.
    private void ApplyTrackMetaData(string? trackMetaData, bool isTrackChange)
    {
        if (SonosValues.IsKnown(trackMetaData))
        {
            if (trackMetaData != _lastTrackMetaData)
            {
                _lastTrack = DidlParser.ParseTrack(trackMetaData);
                _lastTrackMetaData = trackMetaData;
            }

            ApplyTrack(_lastTrack, isTrackChange);
        }
        else if (isTrackChange)
        {
            _lastTrack = null;
            _lastTrackMetaData = null;
            ApplyTrack(null, isTrackChange);
        }
    }

    // Caller holds _stateLock. A placeholder title (connecting, buffering) and missing album art keep the current
    // value while the track stays the same, like NOT_IMPLEMENTED: a station's stream keeps its track URI from song to
    // song, and its polls report the art that its events delivered as absent.
    private void ApplyTrack(DidlTrack? track, bool isTrackChange)
    {
        ApplyTrackTitle(track, isTrackChange);
        CurrentTrackArtist = track?.Artist;
        CurrentTrackAlbum = track?.Album;
        ApplyTrackImage(track, isTrackChange);
    }

    private void ApplyTrackTitle(DidlTrack? track, bool isTrackChange)
    {
        var trackUri = CurrentTrackUri;
        if (!ReferenceEquals(track, _titleTrack) || trackUri != _titleTrackUri)
        {
            var title = track?.Title;
            _title = title is not null && SonosValues.IsTitleOfStreamUri(title, trackUri) ? null : title;
            _titleTrack = track;
            _titleTrackUri = trackUri;
        }

        if (_title is null || SonosValues.IsKnown(_title))
        {
            CurrentTrackTitle = _title;
        }
        else if (isTrackChange)
        {
            CurrentTrackTitle = null;
        }
    }

    private void ApplyTrackImage(DidlTrack? track, bool isTrackChange)
    {
        var baseUri = BaseUri;
        if (!ReferenceEquals(track, _imageUriTrack) || baseUri != _imageUriBaseUri)
        {
            _imageUri = SonosValues.ToAbsoluteUri(track?.AlbumArtUri, baseUri);
            _imageUriTrack = track;
            _imageUriBaseUri = baseUri;
        }

        if (_imageUri is not null || isTrackChange)
        {
            CurrentTrackImageUri = _imageUri;
        }
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
