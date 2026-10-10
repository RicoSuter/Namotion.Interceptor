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

    [Fact]
    public async Task WhenTheTopologyShowsGroupingAllAtOnce_ThenTheWaitEndsAfterOneRead()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(configure: system => system.PollingInterval = TimeSpan.FromHours(1));
        var readsBefore = household.Kitchen.TopologyReadCount;

        // Act
        await household.System.GroupAllAsync("Küche", CancellationToken.None);

        // Assert: one read of the wait and the one of the reconciliation.
        Assert.Equal(2, household.Kitchen.TopologyReadCount - readsBefore);
        Assert.Equal(TestFixtures.KitchenUuid, household.OfficePlayer.GroupCoordinatorUuid);
        Assert.Single(household.System.Groups);
    }

    [Fact]
    public async Task WhenTheTopologyShowsGroupingAllLate_ThenGroupAllReturnsWithEveryRoomInTheGroup()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(configure: system => system.PollingInterval = TimeSpan.FromHours(1));
        var kitchen = household.Kitchen;
        var kitchenMember = (TestFixtures.KitchenUuid, "Küche", kitchen.BaseUri);
        var officeMember = (TestFixtures.OfficeUuid, "Büro", household.Office.BaseUri);
        var standalone = FakeSonosSpeaker.CreateStandaloneTopology(kitchenMember, officeMember);
        kitchen.CallReceived = null;
        household.Office.CallReceived = call =>
        {
            if (call.Action == "SetAVTransportURI")
            {
                // Sonos regroups after answering, so the first read still shows the old groups.
                kitchen.RespondOnce("GetZoneGroupState", ("ZoneGroupState", standalone));
                kitchen.RespondWithGroup(kitchenMember, officeMember);
            }
        };
        var readsBefore = kitchen.TopologyReadCount;

        // Act
        await household.System.GroupAllAsync("Küche", CancellationToken.None);

        // Assert: two reads of the wait and the one of the reconciliation.
        Assert.Equal(3, kitchen.TopologyReadCount - readsBefore);
        Assert.Equal(TestFixtures.KitchenUuid, household.OfficePlayer.GroupCoordinatorUuid);
        Assert.Single(household.System.Groups);
    }

    [Fact]
    public async Task WhenTheTopologyShowsUngroupingAllAtOnce_ThenTheWaitEndsAfterOneRead()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true, configure: system => system.PollingInterval = TimeSpan.FromHours(1));
        var readsBefore = household.Kitchen.TopologyReadCount;

        // Act
        await household.System.UngroupAllAsync(CancellationToken.None);

        // Assert: one read of the wait and the one of the reconciliation.
        Assert.Equal(2, household.Kitchen.TopologyReadCount - readsBefore);
        Assert.True(household.KitchenPlayer.IsGroupCoordinator);
        Assert.Equal(2, household.System.Groups.Count);
    }

    [Fact]
    public async Task WhenTheTopologyShowsUngroupingAllLate_ThenUngroupAllReturnsWithEveryRoomStandalone()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true, configure: system => system.PollingInterval = TimeSpan.FromHours(1));
        var kitchen = household.Kitchen;
        var kitchenMember = (TestFixtures.KitchenUuid, "Küche", kitchen.BaseUri);
        var officeMember = (TestFixtures.OfficeUuid, "Büro", household.Office.BaseUri);
        var grouped = FakeSonosSpeaker.CreateGroupTopology(officeMember, kitchenMember);
        household.Office.CallReceived = null;
        kitchen.CallReceived = call =>
        {
            if (call.Action == "BecomeCoordinatorOfStandaloneGroup")
            {
                // Sonos regroups after answering, so the first read still shows the old group.
                kitchen.RespondOnce("GetZoneGroupState", ("ZoneGroupState", grouped));
                kitchen.RespondWithTopology(kitchenMember, officeMember);
            }
        };
        var readsBefore = kitchen.TopologyReadCount;

        // Act
        await household.System.UngroupAllAsync(CancellationToken.None);

        // Assert: two reads of the wait and the one of the reconciliation.
        Assert.Equal(3, kitchen.TopologyReadCount - readsBefore);
        Assert.True(household.KitchenPlayer.IsGroupCoordinator);
        Assert.Equal(2, household.System.Groups.Count);
    }
}
