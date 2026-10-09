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
}
