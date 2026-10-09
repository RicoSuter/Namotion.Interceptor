using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor.Testing;
using Sonos.Base.Services;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosSystemOperationTests
{
    [Fact]
    public async Task WhenGroupingAllIntoUnknownRoom_ThenThrows()
    {
        // Arrange
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => system.GroupAllAsync("Nowhere", CancellationToken.None));
    }

    [Fact]
    public async Task WhenRefreshingWhileDisconnected_ThenThrows()
    {
        // Arrange
        var system = SonosSystemTopologyTests.CreateSystem();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => system.RefreshAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenRefreshing_ThenTopologyIsReadAgain()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var reads = speaker.Calls.Count(call => call.Action == "GetZoneGroupState");

        // Act
        await connected.System.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.True(speaker.Calls.Count(call => call.Action == "GetZoneGroupState") > reads);
    }

    [Fact]
    public async Task WhenUngroupingAllWhileDisconnected_ThenThrows()
    {
        // Arrange
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => system.UngroupAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenGroupingAll_ThenOtherRoomsJoinTheCoordinator()
    {
        // Arrange
        await using var kitchen = new FakeSonosSpeaker();
        await using var office = new FakeSonosSpeaker();
        var system = await StartHouseholdAsync(kitchen, office);

        try
        {
            // Act
            await system.GroupAllAsync("Küche", CancellationToken.None);

            // Assert
            Assert.Contains(office.Calls, call => call.Action == "SetAVTransportURI" && call.Body.Contains($"x-rincon:{TestFixtures.KitchenUuid}"));
            Assert.DoesNotContain(kitchen.Calls, call => call.Action == "SetAVTransportURI");
        }
        finally
        {
            await system.StopAsync(CancellationToken.None);
            system.Dispose();
        }
    }

    [Fact]
    public async Task WhenGroupingAllFails_ThenFaultPropagatesAndTopologyIsReadAgain()
    {
        // Arrange
        await using var kitchen = new FakeSonosSpeaker();
        await using var office = new FakeSonosSpeaker();
        var system = await StartHouseholdAsync(kitchen, office);
        office.RespondWithFault("SetAVTransportURI", 800);

        try
        {
            var reads = kitchen.Calls.Count(call => call.Action == "GetZoneGroupState");

            // Act
            var exception = await Assert.ThrowsAsync<SonosServiceException>(() => system.GroupAllAsync("Küche", CancellationToken.None));

            // Assert
            Assert.Equal(800, exception.UpnpErrorCode);
            Assert.True(kitchen.Calls.Count(call => call.Action == "GetZoneGroupState") > reads, "The topology was not read again after the failed grouping.");
        }
        finally
        {
            await system.StopAsync(CancellationToken.None);
            system.Dispose();
        }
    }

    private static async Task<SonosSystem> StartHouseholdAsync(FakeSonosSpeaker kitchen, FakeSonosSpeaker office)
    {
        kitchen.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        office.RespondAsIdlePlayer(TestFixtures.OfficeUuid, "Büro");
        var household = new[] { (TestFixtures.KitchenUuid, "Küche", kitchen.BaseUri), (TestFixtures.OfficeUuid, "Büro", office.BaseUri) };
        kitchen.RespondWithTopology(household);
        office.RespondWithTopology(household);

        var system = ConnectedSystem.CreateSystem(kitchen.Host);
        await system.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => system.IsConnected && system.Players.Count == 2 && system.Players.Values.All(player => player.IsConnected),
            ConnectedSystem.WaitTimeout,
            message: "The system should connect to both speakers.");
        return system;
    }
}
