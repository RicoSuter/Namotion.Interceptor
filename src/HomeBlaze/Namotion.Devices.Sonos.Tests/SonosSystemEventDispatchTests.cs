using System.Net;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

/// <summary>
/// Events delivered by a speaker through a running system, from the speaker's NOTIFY to the subject.
/// </summary>
public class SonosSystemEventDispatchTests
{
    private const string RenderingControlEventPath = "/MediaRenderer/RenderingControl/Event";
    private const string GroupRenderingControlEventPath = "/MediaRenderer/GroupRenderingControl/Event";
    private const string TopologyEventPath = "/ZoneGroupTopology/Event";

    [Fact]
    public async Task WhenSpeakerSendsRenderingControlEvent_ThenPlayerVolumeAndMuteUpdateWithoutAPoll()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker, configure: system => system.PollingInterval = TimeSpan.FromHours(1));
        var volumeReads = speaker.Calls.Count(call => call.Action == "GetVolume");

        // Act
        var status = await speaker.NotifyAsync(RenderingControlEventPath, SonosEventBodies.RenderingControl(
            ("Volume", "Master", "12"),
            ("Mute", "Master", "1")));

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        await AsyncTestHelpers.WaitUntilAsync(
            () => connected.Player.Volume == 0.12m && connected.Player.IsMuted == true,
            ConnectedSystem.WaitTimeout,
            message: "The RenderingControl event should reach the player.");
        Assert.Equal(volumeReads, speaker.Calls.Count(call => call.Action == "GetVolume"));
    }

    [Fact]
    public async Task WhenCoordinatorSendsGroupRenderingControlEvent_ThenItsGroupVolumeAndMuteUpdate()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true, configure: system => system.PollingInterval = TimeSpan.FromHours(1));
        var group = household.System.Groups[TestFixtures.OfficeUuid];
        Assert.Equal(0.44m, group.Volume);

        // Act
        var status = await household.Office.NotifyAsync(GroupRenderingControlEventPath, SonosEventBodies.Properties(
            ("GroupVolume", "35"),
            ("GroupMute", "1")));

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        await AsyncTestHelpers.WaitUntilAsync(
            () => group.Volume == 0.35m && group.IsMuted == true,
            ConnectedSystem.WaitTimeout,
            message: "The GroupRenderingControl event of the coordinator should reach its group.");
        Assert.Null(household.Kitchen.GetCallback(GroupRenderingControlEventPath));
    }

    [Fact]
    public async Task WhenSeedSendsATopologyEvent_ThenRoomsRegroupWithoutAPoll()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(configure: system => system.PollingInterval = TimeSpan.FromHours(1));
        Assert.Equal(2, household.System.Groups.Count);
        var topologyReads = household.Kitchen.Calls.Count(call => call.Action == "GetZoneGroupState") +
                            household.Office.Calls.Count(call => call.Action == "GetZoneGroupState");
        var grouped = FakeSonosSpeaker.CreateGroupTopology(
            (TestFixtures.OfficeUuid, "Büro", household.Office.BaseUri),
            (TestFixtures.KitchenUuid, "Küche", household.Kitchen.BaseUri));

        // Act
        var status = await household.Kitchen.NotifyAsync(TopologyEventPath, SonosEventBodies.Properties(("ZoneGroupState", grouped)));

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        await AsyncTestHelpers.WaitUntilAsync(
            () => household.System.Groups.Count == 1,
            ConnectedSystem.WaitTimeout,
            message: "The topology event should merge the rooms into one group.");
        var group = Assert.Single(household.System.Groups).Value;
        Assert.Same(household.OfficePlayer, group.Coordinator);
        Assert.Equal([household.OfficePlayer, household.KitchenPlayer], group.Members);
        Assert.Equal(TestFixtures.OfficeUuid, household.KitchenPlayer.GroupCoordinatorUuid);
        Assert.Equal(topologyReads,
            household.Kitchen.Calls.Count(call => call.Action == "GetZoneGroupState") +
            household.Office.Calls.Count(call => call.Action == "GetZoneGroupState"));
    }
}
