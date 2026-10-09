using Namotion.Devices.Sonos.Tests.Testing;
using Sonos.Base.Services;
using Xunit;
using static Namotion.Devices.Sonos.Tests.Testing.TestFixtures;

namespace Namotion.Devices.Sonos.Tests;

public class SonosSystemOperationTests
{
    [Fact]
    public async Task WhenGroupingAllIntoUnknownRoom_ThenThrows()
    {
        // Arrange
        var system = CreateHousehold();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => system.GroupAllAsync("Nowhere", CancellationToken.None));
    }

    [Fact]
    public void WhenRefreshingWhileDisconnected_ThenReturnsFaultedTask()
    {
        // Arrange
        var system = CreateSystem();

        // Act
        var task = system.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.Contains("not connected", Assert.IsType<InvalidOperationException>(task.Exception?.InnerException).Message);
    }

    [Fact]
    public void WhenGroupingAllWhileDisconnected_ThenReturnsFaultedTask()
    {
        // Arrange
        var system = CreateHousehold();

        // Act
        var task = system.GroupAllAsync("Küche", CancellationToken.None);

        // Assert
        Assert.Contains("not connected", Assert.IsType<InvalidOperationException>(task.Exception?.InnerException).Message);
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
    public void WhenUngroupingAllWhileDisconnected_ThenReturnsFaultedTask()
    {
        // Arrange
        var system = CreateHousehold();

        // Act
        var task = system.UngroupAllAsync(CancellationToken.None);

        // Assert
        Assert.Contains("not connected", Assert.IsType<InvalidOperationException>(task.Exception?.InnerException).Message);
    }

    [Fact]
    public async Task WhenGroupingAllIntoNullRoom_ThenThrowsArgumentNull()
    {
        // Arrange
        var system = CreateHousehold();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => system.GroupAllAsync(null!, CancellationToken.None));
        Assert.Equal("room", exception.ParamName);
    }

    [Fact]
    public async Task WhenGroupingAll_ThenOtherRoomsJoinTheCoordinator()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync();

        // Act
        await household.System.GroupAllAsync("Küche", CancellationToken.None);

        // Assert
        Assert.Contains(household.Office.Calls, call => call.Action == "SetAVTransportURI" && call.GetArgument("CurrentURI") == $"x-rincon:{TestFixtures.KitchenUuid}");
        Assert.DoesNotContain(household.Kitchen.Calls, call => call.Action == "SetAVTransportURI");
    }

    [Fact]
    public async Task WhenGroupingAllFails_ThenFaultPropagatesAndTopologyIsReadAgain()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync();
        household.Office.RespondWithFault("SetAVTransportURI", 800);
        var reads = household.Kitchen.Calls.Count(call => call.Action == "GetZoneGroupState");

        // Act
        var exception = await Assert.ThrowsAsync<SonosServiceException>(() => household.System.GroupAllAsync("Küche", CancellationToken.None));

        // Assert
        Assert.Equal(800, exception.UpnpErrorCode);
        Assert.True(household.Kitchen.Calls.Count(call => call.Action == "GetZoneGroupState") > reads, "The topology was not read again after the failed grouping.");
    }

    [Fact]
    public async Task WhenGroupingSucceedsButTopologyReadFails_ThenGroupingCompletes()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync();
        household.Kitchen.RespondWithFault("GetZoneGroupState", 501);
        var reads = household.Kitchen.Calls.Count(call => call.Action == "GetZoneGroupState");

        // Act
        await household.System.GroupAllAsync("Küche", CancellationToken.None);

        // Assert
        Assert.Contains(household.Office.Calls, call => call.Action == "SetAVTransportURI" && call.GetArgument("CurrentURI") == $"x-rincon:{TestFixtures.KitchenUuid}");
        Assert.True(household.Kitchen.Calls.Count(call => call.Action == "GetZoneGroupState") > reads, "The topology read after grouping was not attempted.");
    }
}
