using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Media;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// Rooms playing together. Playback and track state are the coordinator's; volume and mute are the group's.
/// </summary>
[InterceptorSubject]
public partial class SonosGroup :
    IAudioPlayer,
    IMediaTrackState,
    IVirtualSubject,
    ITitleProvider,
    IIconProvider
{
    private readonly SonosSystem _system;
    private readonly Lock _stateLock = new();
    private DateTimeOffset _lastEventAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPollStartedAt = DateTimeOffset.MinValue;

    internal SonosGroup(SonosSystem system, SonosPlayer coordinator)
    {
        _system = system;
        GroupId = string.Empty;
        Coordinator = coordinator;
        Members = [coordinator];
    }

    /// <summary>
    /// The current Sonos group id. It changes on every regroup, so the group is keyed by its coordinator instead.
    /// </summary>
    [State(Position = 1)]
    public partial string GroupId { get; internal set; }

    [State(Position = 2)]
    public partial SonosPlayer Coordinator { get; internal set; }

    [State(Position = 3)]
    public partial SonosPlayer[] Members { get; internal set; }

    public partial decimal? Volume { get; internal set; }

    public partial bool? IsMuted { get; internal set; }

    [Derived]
    public bool? IsPlaying => Coordinator.IsPlaying;

    [Derived]
    public string? CurrentTrackTitle => Coordinator.CurrentTrackTitle;

    [Derived]
    public string? CurrentTrackArtist => Coordinator.CurrentTrackArtist;

    [Derived]
    public string? CurrentTrackAlbum => Coordinator.CurrentTrackAlbum;

    [Derived]
    public string? CurrentTrackImageUri => Coordinator.CurrentTrackImageUri;

    [Derived]
    public string? CurrentTrackUri => Coordinator.CurrentTrackUri;

    [Derived]
    public TimeSpan? CurrentTrackPosition => Coordinator.CurrentTrackPosition;

    [Derived]
    public TimeSpan? CurrentTrackDuration => Coordinator.CurrentTrackDuration;

    [Derived]
    public string? Title => string.Join(" + ", Members.Select(member => member.RoomName));

    [Derived]
    public string? IconName => "SpeakerGroup";

    private bool CanControl => Coordinator.IsConnected && _system.IsConnected;

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
    [PropertyAttribute("SetVolume", KnownAttributes.IsEnabled)]
    public bool SetVolume_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("ChangeVolume", KnownAttributes.IsEnabled)]
    public bool ChangeVolume_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Mute", KnownAttributes.IsEnabled)]
    public bool Mute_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("Unmute", KnownAttributes.IsEnabled)]
    public bool Unmute_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("PlayFavorite", KnownAttributes.IsEnabled)]
    public bool PlayFavorite_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("PlayUri", KnownAttributes.IsEnabled)]
    public bool PlayUri_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("PlayStream", KnownAttributes.IsEnabled)]
    public bool PlayStream_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("SetShuffle", KnownAttributes.IsEnabled)]
    public bool SetShuffle_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("SetRepeat", KnownAttributes.IsEnabled)]
    public bool SetRepeat_IsEnabled => CanControl;

    [Derived]
    [PropertyAttribute("SetSleepTimer", KnownAttributes.IsEnabled)]
    public bool SetSleepTimer_IsEnabled => CanControl;

    [Operation(Title = "Play", Icon = "PlayArrow", Position = 1, Description = "Starts or resumes playback of the group.")]
    public Task PlayAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.PlayAsync(token), cancellationToken);

    [Operation(Title = "Pause", Icon = "Pause", Position = 2, Description = "Pauses playback of the group.")]
    public Task PauseAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.PauseAsync(token), cancellationToken);

    [Operation(Title = "Stop", Icon = "Stop", Position = 3, Description = "Stops playback of the group.")]
    public Task StopAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.StopAsync(token), cancellationToken);

    [Operation(Title = "Next", Icon = "SkipNext", Position = 4, Description = "Skips to the next track of the group.")]
    public Task NextAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.NextAsync(token), cancellationToken);

    [Operation(Title = "Previous", Icon = "SkipPrevious", Position = 5, Description = "Goes back to the previous track of the group.")]
    public Task PreviousAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.PreviousAsync(token), cancellationToken);

    [Operation(Title = "Play or Pause", Position = 6, Description = "Pauses the group when it plays, otherwise starts playback.")]
    public Task TogglePlaybackAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.TogglePlaybackAsync(token), cancellationToken);

    [Operation(Title = "Seek", Position = 7, Description = "Seeks the current track of the group to the given position.")]
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        return RunAsync((connection, token) => connection.SeekAsync(position, token), cancellationToken);
    }

    [Operation(Title = "Set Volume", Position = 10, Description = "Sets the group volume.")]
    public Task SetVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal volume, CancellationToken cancellationToken)
    {
        SonosValues.ThrowIfVolumeOutOfRange(volume);
        return RunAsync((connection, token) => connection.SetGroupVolumeAsync(SonosValues.ToSonosVolume(volume), token), cancellationToken);
    }

    [Operation(Title = "Change Volume", Position = 11, Description = "Changes the group volume by a relative amount.")]
    public Task ChangeVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal delta, CancellationToken cancellationToken)
    {
        SonosValues.ThrowIfVolumeAdjustmentOutOfRange(delta);
        return RunAsync((connection, token) => connection.ChangeGroupVolumeAsync(SonosValues.ToSonosVolumeAdjustment(delta), token), cancellationToken);
    }

    [Operation(Title = "Mute", Icon = "VolumeOff", Position = 12, Description = "Mutes every player in the group.")]
    public Task MuteAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.SetGroupMuteAsync(true, token), cancellationToken);

    [Operation(Title = "Unmute", Icon = "VolumeUp", Position = 13, Description = "Unmutes every player in the group.")]
    public Task UnmuteAsync(CancellationToken cancellationToken) =>
        RunAsync((connection, token) => connection.SetGroupMuteAsync(false, token), cancellationToken);

    // Content, play mode and sleep timer are group state, so the coordinator's operations already target the group.

    [Operation(Title = "Play Favorite", Position = 20, Description = "Plays the Sonos favorite with the given title on the group.")]
    public Task PlayFavoriteAsync(string title, CancellationToken cancellationToken) =>
        Coordinator.PlayFavoriteAsync(title, cancellationToken);

    /// <inheritdoc cref="SonosPlayer.PlayUriAsync"/>
    [Operation(Title = "Play URI", Position = 21, Description = "Plays a URI once as a normal track on the group, which ends and can be sought.")]
    public Task PlayUriAsync(string uri, CancellationToken cancellationToken) =>
        Coordinator.PlayUriAsync(uri, cancellationToken);

    /// <inheritdoc cref="SonosPlayer.PlayStreamAsync"/>
    [Operation(Title = "Play Stream", Position = 22, Description = "Plays an http(s) or x-rincon-mp3radio stream on the group as radio, which Sonos reconnects when it ends.")]
    public Task PlayStreamAsync(string uri, string? title, CancellationToken cancellationToken) =>
        Coordinator.PlayStreamAsync(uri, title, cancellationToken);

    [Operation(Title = "Set Shuffle", Position = 30, Description = "Turns shuffle on or off for the group.")]
    public Task SetShuffleAsync(bool shuffle, CancellationToken cancellationToken) =>
        Coordinator.SetShuffleAsync(shuffle, cancellationToken);

    [Operation(Title = "Set Repeat", Position = 31, Description = "Sets the repeat mode of the group.")]
    public Task SetRepeatAsync(SonosRepeatMode repeat, CancellationToken cancellationToken) =>
        Coordinator.SetRepeatAsync(repeat, cancellationToken);

    /// <inheritdoc cref="SonosPlayer.SetSleepTimerAsync"/>
    [Operation(Title = "Set Sleep Timer", Position = 32, Description = "Sets the sleep timer of the group, at most 23:59:59; zero cancels it.")]
    public Task SetSleepTimerAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Coordinator.SetSleepTimerAsync(duration, cancellationToken);

    private async Task RunAsync(Func<SonosConnection, CancellationToken, Task> command, CancellationToken cancellationToken)
    {
        var coordinator = Coordinator;
        var connection = _system.GetConnectionForCommand(coordinator.Uuid);
        try
        {
            await command(connection, cancellationToken);
        }
        finally
        {
            // Also after a failure: a multi-step command may have partly applied. The refresh logs its own failures.
            await _system.RefreshAfterCommandAsync(coordinator, cancellationToken);
        }
    }

    internal void Update(string groupId, SonosPlayer[] members)
    {
        GroupId = groupId;
        if (!members.SequenceEqual(Members))
        {
            Members = members;
        }
    }

    internal void ApplyGroupRenderingControlEvent(GroupRenderingControlChange change, DateTimeOffset receivedAt)
    {
        lock (_stateLock)
        {
            _lastEventAt = receivedAt;
            Apply(change);
        }
    }

    internal void ApplyGroupRenderingControlPoll(GroupRenderingControlChange change, DateTimeOffset pollStartedAt)
    {
        lock (_stateLock)
        {
            // Command refreshes poll outside the reconciliation, so an older poll can complete after a newer one.
            if (SonosValues.IsSupersededPoll(pollStartedAt, _lastPollStartedAt))
            {
                return;
            }

            _lastPollStartedAt = pollStartedAt;
            if (_lastEventAt <= pollStartedAt)
            {
                Apply(change);
            }
        }
    }

    private void Apply(GroupRenderingControlChange change)
    {
        if (change.Volume is { } volume)
        {
            Volume = SonosValues.ToVolume(volume);
        }

        if (change.Mute is { } mute)
        {
            IsMuted = mute;
        }
    }
}
