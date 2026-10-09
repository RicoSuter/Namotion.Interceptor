using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosGroupOperationTests
{
    [Fact]
    public async Task WhenSettingGroupVolume_ThenSnapshotPrecedesSetGroupVolume()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var group = Assert.Single(connected.System.Groups).Value;

        // Act
        await group.SetVolumeAsync(0.3m, CancellationToken.None);

        // Assert
        var actions = speaker.Calls.Select(call => call.Action).ToList();
        var snapshot = actions.IndexOf("SnapshotGroupVolume");
        var setVolume = actions.IndexOf("SetGroupVolume");
        Assert.True(snapshot >= 0 && setVolume > snapshot);
        Assert.Contains(speaker.Calls, call => call.Action == "SetGroupVolume" && call.Body.Contains("<DesiredVolume>30</DesiredVolume>"));
    }

    [Fact]
    public async Task WhenGroupSystemIsNotConnected_ThenPauseThrows()
    {
        // Arrange
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
        var group = system.Groups[TestFixtures.LivingRoomUuid];

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => group.PauseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenGroupHasMembers_ThenVolumeIsSentToTheCoordinatorOnly()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true);
        var group = household.System.Groups[TestFixtures.OfficeUuid];
        Assert.Equal(2, group.Members.Length);

        // Act
        await group.SetVolumeAsync(0.3m, CancellationToken.None);

        // Assert
        Assert.Contains(household.Office.Calls, call => call.Action == "SetGroupVolume");
        Assert.DoesNotContain(household.Kitchen.Calls, call => call.Action == "SetGroupVolume");
    }

    [Fact]
    public void WhenGroupSystemIsNotConnected_ThenOperationsAreDisabled()
    {
        // Arrange
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
        var group = system.Groups[TestFixtures.LivingRoomUuid];

        // Assert
        Assert.False(group.Stop_IsEnabled);
        Assert.False(group.Next_IsEnabled);
        Assert.False(group.Previous_IsEnabled);
        Assert.False(group.TogglePlayback_IsEnabled);
        Assert.False(group.SetVolume_IsEnabled);
        Assert.False(group.ChangeVolume_IsEnabled);
        Assert.False(group.Mute_IsEnabled);
        Assert.False(group.Unmute_IsEnabled);
    }

    [Fact]
    public async Task WhenGroupIsConnected_ThenOperationsAreEnabled()
    {
        // Act
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var group = Assert.Single(connected.System.Groups).Value;

        // Assert
        Assert.True(group.Stop_IsEnabled);
        Assert.True(group.Next_IsEnabled);
        Assert.True(group.Previous_IsEnabled);
        Assert.True(group.TogglePlayback_IsEnabled);
        Assert.True(group.SetVolume_IsEnabled);
        Assert.True(group.ChangeVolume_IsEnabled);
        Assert.True(group.Mute_IsEnabled);
        Assert.True(group.Unmute_IsEnabled);
    }
}
