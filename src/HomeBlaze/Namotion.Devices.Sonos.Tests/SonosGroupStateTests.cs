using System.Reflection;
using HomeBlaze.Abstractions.Media;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;
using static Namotion.Devices.Sonos.Tests.Testing.TestFixtures;

namespace Namotion.Devices.Sonos.Tests;

public class SonosGroupStateTests
{
    private const string MemberTransportUri = "x-rincon:" + OfficeUuid;
    private const string CoordinatorTrackUri = "x-file-cifs://nas/music/song.mp3";

    // The office coordinator plays a queue track of a playlist.
    private static SonosPlayerReading CoordinatorPlaying() => new(
        new AvTransportChange(
            "PLAYING",
            "NORMAL",
            "x-rincon-queue:" + OfficeUuid + "#0",
            CoordinatorTrackUri,
            "0:03:25",
            SonosEventBodies.Didl("Song", "Artist", "Album", "/getaa?s=1&u=x"),
            SonosEventBodies.Didl("Playlist")),
        TimeSpan.FromSeconds(42),
        null,
        EmptyRenderingControl);

    // A grouped member reports a transport that points at its coordinator, without track details.
    private static SonosPlayerReading MemberFollowing(string transportState) => new(
        new AvTransportChange(transportState, "NORMAL", MemberTransportUri, MemberTransportUri, "0:00:00", ""),
        TimeSpan.Zero,
        null,
        EmptyRenderingControl);

    [Fact]
    public void WhenCoordinatorPlays_ThenMemberReportsTheCoordinatorsPlaybackAndTrack()
    {
        // Arrange
        var system = CreateGroupedSystem();
        var coordinator = system.Players[OfficeUuid];
        var member = system.Players[KitchenUuid];

        // Act
        coordinator.ApplyPoll(CoordinatorPlaying(), T0);
        member.ApplyPoll(MemberFollowing("STOPPED"), T0);

        // Assert
        Assert.Equal(MediaPlaybackState.Playing, member.PlaybackState);
        Assert.True(member.IsPlaying);
        Assert.Equal("Song", member.CurrentTrackTitle);
        Assert.Equal("Artist", member.CurrentTrackArtist);
        Assert.Equal("Album", member.CurrentTrackAlbum);
        Assert.Equal("http://10.0.0.116:1400/getaa?s=1&u=x", member.CurrentTrackImageUri);
        Assert.Equal(CoordinatorTrackUri, member.CurrentTrackUri);
        Assert.Equal(TimeSpan.FromSeconds(42), member.CurrentTrackPosition);
        Assert.Equal(TimeSpan.FromSeconds(205), member.CurrentTrackDuration);
        Assert.Equal("Playlist", member.SourceTitle);
    }

    [Fact]
    public void WhenCoordinatorTrackHasADuration_ThenSeekIsEnabledOnTheMember()
    {
        // Arrange
        var system = CreateGroupedSystem();
        var member = system.Players[KitchenUuid];
        member.ApplyPoll(MemberFollowing("PLAYING"), T0);
        Assert.False(member.Seek_IsEnabled);

        // Act
        system.Players[OfficeUuid].ApplyPoll(CoordinatorPlaying(), T0);

        // Assert
        Assert.True(member.Seek_IsEnabled);
    }

    private static string StandaloneTopology() => SonosEventBodies.CreateStandaloneTopology(
        (OfficeUuid, "Büro", new Uri("http://10.0.0.116:1400/")),
        (KitchenUuid, "Küche", new Uri("http://10.0.0.121:1400/")));

    // The kitchen plays its own queue again after leaving a group.
    private static SonosPlayerReading KitchenPausedOnItsOwnTrack() => new(
        new AvTransportChange(
            "PAUSED_PLAYBACK",
            "NORMAL",
            "x-rincon-queue:" + KitchenUuid + "#0",
            "x-file-cifs://nas/music/own.mp3",
            "0:02:00",
            SonosEventBodies.Didl("Own Song", "Own Artist")),
        TimeSpan.FromSeconds(5),
        null,
        EmptyRenderingControl);

    [Fact]
    public void WhenMemberLeavesTheGroup_ThenItForgetsWhatItReportedAsAMember()
    {
        // Arrange
        var system = CreateGroupedSystem();
        var coordinator = system.Players[OfficeUuid];
        var member = system.Players[KitchenUuid];
        coordinator.ApplyPoll(CoordinatorPlaying(), T0);
        member.ApplyPoll(MemberFollowing("STOPPED"), T0);
        Assert.Equal("Song", member.CurrentTrackTitle);

        // Act
        system.ApplyTopology(ZoneGroupStateParser.Parse(StandaloneTopology()));

        // Assert
        Assert.Null(member.PlaybackState);
        Assert.Null(member.IsPlaying);
        Assert.Null(member.CurrentTrackUri);
        Assert.Null(member.CurrentTrackTitle);
        Assert.Null(member.CurrentTrackArtist);
        Assert.Null(member.CurrentTrackAlbum);
        Assert.Null(member.CurrentTrackImageUri);
        Assert.Null(member.CurrentTrackPosition);
        Assert.Null(member.CurrentTrackDuration);
        Assert.Null(member.Source);
        Assert.Null(member.SourceTitle);
        Assert.Equal("Song", coordinator.CurrentTrackTitle);
        Assert.Equal(MediaPlaybackState.Playing, coordinator.PlaybackState);
    }

    [Fact]
    public void WhenAStandalonePlayerIsReadAfterLeavingAGroup_ThenItReportsItsOwnStateAgain()
    {
        // Arrange
        var system = CreateGroupedSystem();
        var coordinator = system.Players[OfficeUuid];
        var member = system.Players[KitchenUuid];
        coordinator.ApplyPoll(CoordinatorPlaying(), T0);
        member.ApplyPoll(MemberFollowing("PLAYING"), T0);
        system.ApplyTopology(ZoneGroupStateParser.Parse(StandaloneTopology()));

        // Act
        member.ApplyPoll(KitchenPausedOnItsOwnTrack(), T0 + 1);

        // Assert
        Assert.Equal(MediaPlaybackState.Paused, member.PlaybackState);
        Assert.Equal("Own Song", member.CurrentTrackTitle);
        Assert.Equal("Own Artist", member.CurrentTrackArtist);
        Assert.Equal("x-file-cifs://nas/music/own.mp3", member.CurrentTrackUri);
        Assert.Equal(TimeSpan.FromSeconds(5), member.CurrentTrackPosition);
        Assert.Equal(SonosSource.Queue, member.Source);
        Assert.Equal("Song", coordinator.CurrentTrackTitle);
    }

    [Fact]
    public void WhenAMemberReportedItsOwnTransportBeforeTheTopologyShowsItLeft_ThenThatStateIsKept()
    {
        // Arrange
        var system = CreateGroupedSystem();
        var member = system.Players[KitchenUuid];
        system.Players[OfficeUuid].ApplyPoll(CoordinatorPlaying(), T0);
        member.ApplyPoll(MemberFollowing("PLAYING"), T0);
        member.ApplyAvTransportEvent(KitchenPausedOnItsOwnTrack().AvTransport, T0 + 1);
        Assert.Equal("Song", member.CurrentTrackTitle);

        // Act
        system.ApplyTopology(ZoneGroupStateParser.Parse(StandaloneTopology()));

        // Assert
        Assert.Equal(MediaPlaybackState.Paused, member.PlaybackState);
        Assert.Equal("Own Song", member.CurrentTrackTitle);
        Assert.Equal("x-file-cifs://nas/music/own.mp3", member.CurrentTrackUri);
        Assert.Equal(SonosSource.Queue, member.Source);
    }

    [Fact]
    public void WhenAPollThatReadTheMemberStateCompletesAfterTheRegroup_ThenTheMemberStateDoesNotComeBack()
    {
        // Arrange
        var system = CreateGroupedSystem();
        var member = system.Players[KitchenUuid];
        system.Players[OfficeUuid].ApplyPoll(CoordinatorPlaying(), system.NextOrder());
        var pollStartedAt = system.NextOrder();
        system.ApplyTopology(ZoneGroupStateParser.Parse(StandaloneTopology()));

        // Act
        member.ApplyPoll(MemberFollowing("PLAYING"), pollStartedAt);

        // Assert
        Assert.Null(member.CurrentTrackUri);
        Assert.Null(member.PlaybackState);
        Assert.Null(member.Source);
    }

    [Fact]
    public void WhenTheCoordinatorHandsOverToAMember_ThenTheNewCoordinatorForgetsItsMemberState()
    {
        // Arrange
        var system = CreateGroupedSystem();
        var office = system.Players[OfficeUuid];
        var kitchen = system.Players[KitchenUuid];
        office.ApplyPoll(CoordinatorPlaying(), T0);
        kitchen.ApplyPoll(MemberFollowing("PLAYING"), T0);

        // Act
        system.ApplyTopology(ZoneGroupStateParser.Parse(SonosEventBodies.CreateGroupTopology(
            (KitchenUuid, "Küche", new Uri("http://10.0.0.121:1400/")),
            (OfficeUuid, "Büro", new Uri("http://10.0.0.116:1400/")))));

        // Assert
        Assert.True(kitchen.IsGroupCoordinator);
        Assert.Null(kitchen.CurrentTrackUri);
        Assert.Null(kitchen.PlaybackState);
        Assert.Null(office.CurrentTrackUri);
        Assert.Null(office.CurrentTrackTitle);
        Assert.Null(office.PlaybackState);
        Assert.Null(system.Groups[KitchenUuid].CurrentTrackTitle);
    }

    [Fact]
    public void WhenAMembersCoordinatorChangesToAnotherPlayer_ThenTheMemberReportsTheNewCoordinatorsTrack()
    {
        // Arrange
        var system = CreateSystem();
        system.ApplyTopology(new SonosTopology([TopologyGroup(OfficeUuid, KitchenUuid), TopologyGroup(LivingRoomUuid)]));
        ReportAllReachable(system);
        var member = system.Players[KitchenUuid];
        system.Players[OfficeUuid].ApplyPoll(CoordinatorPlaying(), T0);
        system.Players[LivingRoomUuid].ApplyAvTransportEvent(
            new AvTransportChange("PAUSED_PLAYBACK", "NORMAL", "x-sonosapi-stream:s1?sid=303", "x-sonosapi-stream:s1?sid=303", null, SonosEventBodies.Didl("Other Song", "Other Artist")),
            T0);
        Assert.Equal("Song", member.CurrentTrackTitle);

        // Act
        system.ApplyTopology(new SonosTopology([TopologyGroup(LivingRoomUuid, KitchenUuid), TopologyGroup(OfficeUuid)]));

        // Assert
        Assert.Equal(LivingRoomUuid, member.GroupCoordinatorUuid);
        Assert.Equal("Other Song", member.CurrentTrackTitle);
        Assert.Equal("Other Artist", member.CurrentTrackArtist);
        Assert.Equal(MediaPlaybackState.Paused, member.PlaybackState);
        Assert.Equal(SonosSource.Radio, member.Source);
        Assert.Equal("Song", system.Players[OfficeUuid].CurrentTrackTitle);
    }

    // A group of the given players, coordinated by the first.
    private static SonosTopologyGroup TopologyGroup(params string[] uuids) => new(
        uuids[0] + ":1",
        uuids[0],
        uuids.Select(uuid => new SonosTopologyPlayer(uuid, uuid, new Uri("http://10.0.0.1:1400/"), null, null, null, [])).ToArray());

    [Fact]
    public void WhenGroupIsInspected_ThenItsSonosGroupIdIsNotPublic()
    {
        // Act
        var property = typeof(SonosGroup).GetProperty("GroupId", BindingFlags.Public | BindingFlags.Instance);

        // Assert
        Assert.Null(property);
    }

    [Fact]
    public void WhenPlayerIsAGroupMember_ThenSourcePlayModeAndSleepTimerFollowTheCoordinator()
    {
        // Arrange
        const string spotifyUri = "x-sonos-vli:RINCON_A0000000000701400:2,spotify:0123456789abcdef";
        var system = CreateGroupedSystem();
        var coordinator = system.Players[TestFixtures.OfficeUuid];
        var member = system.Players[TestFixtures.KitchenUuid];
        var noRenderingControl = EmptyRenderingControl;
        var memberTransport = $"x-rincon:{TestFixtures.OfficeUuid}";

        // Act
        coordinator.ApplyPoll(
            new SonosPlayerReading(new AvTransportChange("PLAYING", "SHUFFLE", spotifyUri, spotifyUri, null, null, SonosEventBodies.Didl("Spotify")), null, TimeSpan.FromMinutes(30), noRenderingControl),
            T0);
        member.ApplyPoll(
            new SonosPlayerReading(new AvTransportChange("PLAYING", "NORMAL", memberTransport, memberTransport, null, null), null, null, noRenderingControl),
            T0);

        // Assert
        Assert.Equal("Spotify", member.SourceTitle);
        Assert.Equal("Spotify", system.Groups[TestFixtures.OfficeUuid].SourceTitle);
        Assert.Equal(SonosSource.SpotifyConnect, member.Source);
        Assert.True(member.Shuffle);
        Assert.Equal(SonosRepeatMode.All, member.Repeat);
        Assert.Equal(TimeSpan.FromMinutes(30), member.SleepTimerRemaining);
    }

    [Fact]
    public void WhenCoordinatorReportsSourcePlayModeAndSleepTimer_ThenTheGroupReportsThem()
    {
        // Arrange
        var system = CreateGroupedSystem();
        var group = system.Groups[OfficeUuid];
        Assert.Null(group.Source);
        Assert.Null(group.Shuffle);
        Assert.Null(group.Repeat);
        Assert.Null(group.SleepTimerRemaining);

        // Act
        system.Players[OfficeUuid].ApplyPoll(
            new SonosPlayerReading(
                new AvTransportChange("PLAYING", "SHUFFLE_REPEAT_ONE", SpotifyConnectUri, SpotifyConnectUri, null, null),
                null,
                TimeSpan.FromMinutes(30),
                EmptyRenderingControl),
            T0);

        // Assert
        Assert.Equal(SonosSource.SpotifyConnect, group.Source);
        Assert.True(group.Shuffle);
        Assert.Equal(SonosRepeatMode.One, group.Repeat);
        Assert.Equal(TimeSpan.FromMinutes(30), group.SleepTimerRemaining);
    }

    [Fact]
    public void WhenGroupRenderingControlEventApplied_ThenVolumeIsAFraction()
    {
        // Arrange
        var group = CreateHousehold().Groups[TestFixtures.LivingRoomUuid];

        // Act
        group.ApplyGroupRenderingControlEvent(new GroupRenderingControlChange(35, true), T0);

        // Assert
        Assert.Equal(0.35m, group.Volume);
        Assert.True(group.IsMuted);
    }

    [Fact]
    public void WhenGroupPollStartedBeforeEvent_ThenEventIsKept()
    {
        // Arrange
        var group = CreateHousehold().Groups[TestFixtures.LivingRoomUuid];
        group.ApplyGroupRenderingControlEvent(new GroupRenderingControlChange(35, false), T0 + 1);

        // Act
        group.ApplyGroupRenderingControlPoll(new GroupRenderingControlChange(10, false), T0);

        // Assert
        Assert.Equal(0.35m, group.Volume);
    }

    [Fact]
    public void WhenOlderGroupPollCompletesAfterNewerPoll_ThenOlderPollIsDropped()
    {
        // Arrange
        var group = CreateHousehold().Groups[TestFixtures.LivingRoomUuid];
        group.ApplyGroupRenderingControlPoll(new GroupRenderingControlChange(35, false), T0 + 2);

        // Act
        group.ApplyGroupRenderingControlPoll(new GroupRenderingControlChange(10, true), T0 + 1);

        // Assert
        Assert.Equal(0.35m, group.Volume);
        Assert.False(group.IsMuted);
    }

    [Fact]
    public void WhenALaterOrderedGroupPollArrives_ThenItApplies()
    {
        // Arrange
        var system = CreateHousehold();
        var group = system.Groups[LivingRoomUuid];
        group.ApplyGroupRenderingControlPoll(new GroupRenderingControlChange(35, false), system.NextOrder());

        // Act
        group.ApplyGroupRenderingControlPoll(new GroupRenderingControlChange(10, true), system.NextOrder());
        group.ApplyGroupRenderingControlPoll(new GroupRenderingControlChange(20, true), system.NextOrder());

        // Assert
        Assert.Equal(0.2m, group.Volume);
        Assert.True(group.IsMuted);
    }

    [Fact]
    public void WhenCoordinatorPlays_ThenGroupReportsCoordinatorTrack()
    {
        // Arrange
        var system = CreateReachableHousehold();
        var group = system.Groups[TestFixtures.LivingRoomUuid];

        // Act
        system.Players[TestFixtures.LivingRoomUuid].ApplyAvTransportEvent(
            new AvTransportChange("PLAYING", null, null, "x-sonos-htastream:x:spdif", null, SonosEventBodies.Didl("Song", "Artist")),
            T0);

        // Assert
        Assert.Equal(MediaPlaybackState.Playing, group.PlaybackState);
        Assert.True(group.IsPlaying);
        Assert.Equal("Song", group.CurrentTrackTitle);
        Assert.Equal("Artist", group.CurrentTrackArtist);
    }
}
