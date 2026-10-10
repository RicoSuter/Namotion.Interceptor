using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;
using static Namotion.Devices.Sonos.Tests.Testing.TestFixtures;

namespace Namotion.Devices.Sonos.Tests;

public class SonosGroupStateTests
{

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
        Assert.Equal("Spotify", member.MediaTitle);
        Assert.Equal("Spotify", system.Groups[TestFixtures.OfficeUuid].MediaTitle);
        Assert.Equal(SonosSource.SpotifyConnect, member.Source);
        Assert.True(member.Shuffle);
        Assert.Equal(SonosRepeatMode.All, member.Repeat);
        Assert.Equal(TimeSpan.FromMinutes(30), member.SleepTimerRemaining);
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
        var system = CreateHousehold();
        var group = system.Groups[TestFixtures.LivingRoomUuid];

        // Act
        system.Players[TestFixtures.LivingRoomUuid].ApplyAvTransportEvent(
            new AvTransportChange("PLAYING", null, null, "x-sonos-htastream:x:spdif", null, SonosEventBodies.Didl("Song", "Artist")),
            T0);

        // Assert
        Assert.True(group.IsPlaying);
        Assert.Equal("Song", group.CurrentTrackTitle);
        Assert.Equal("Artist", group.CurrentTrackArtist);
    }
}
