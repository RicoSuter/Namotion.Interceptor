using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosGroupStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static SonosSystem CreateSystem()
    {
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
        return system;
    }

    [Fact]
    public void WhenGroupRenderingControlEventApplied_ThenVolumeIsAFraction()
    {
        // Arrange
        var group = CreateSystem().Groups[TestFixtures.LivingRoomUuid];

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
        var group = CreateSystem().Groups[TestFixtures.LivingRoomUuid];
        group.ApplyGroupRenderingControlEvent(new GroupRenderingControlChange(35, false), T0.AddSeconds(1));

        // Act
        group.ApplyGroupRenderingControlPoll(new GroupRenderingControlChange(10, false), T0);

        // Assert
        Assert.Equal(0.35m, group.Volume);
    }

    [Fact]
    public void WhenOlderGroupPollCompletesAfterNewerPoll_ThenOlderPollIsDropped()
    {
        // Arrange
        var group = CreateSystem().Groups[TestFixtures.LivingRoomUuid];
        group.ApplyGroupRenderingControlPoll(new GroupRenderingControlChange(35, false), T0.AddSeconds(2));

        // Act
        group.ApplyGroupRenderingControlPoll(new GroupRenderingControlChange(10, true), T0.AddSeconds(1));

        // Assert
        Assert.Equal(0.35m, group.Volume);
        Assert.False(group.IsMuted);
    }

    [Fact]
    public void WhenGroupPollStartsLongBeforeTheLastPoll_ThenItIsTakenAsAClockJumpAndApplied()
    {
        // Arrange
        var group = CreateSystem().Groups[TestFixtures.LivingRoomUuid];
        group.ApplyGroupRenderingControlPoll(new GroupRenderingControlChange(35, false), T0.AddMinutes(10));

        // Act
        group.ApplyGroupRenderingControlPoll(new GroupRenderingControlChange(10, true), T0);

        // Assert
        Assert.Equal(0.1m, group.Volume);
        Assert.True(group.IsMuted);
    }

    [Fact]
    public void WhenCoordinatorPlays_ThenGroupReportsCoordinatorTrack()
    {
        // Arrange
        var system = CreateSystem();
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
