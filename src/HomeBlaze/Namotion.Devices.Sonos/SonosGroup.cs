using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Media;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// Rooms playing together. Playback and track state are the coordinator's; volume and mute are the group's.
/// </summary>
[InterceptorSubject]
public partial class SonosGroup :
    IAudioPlayerState,
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
    [PropertyAttribute("Seek", KnownAttributes.IsEnabled)]
    public bool Seek_IsEnabled => CanControl && CurrentTrackDuration > TimeSpan.Zero;

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
