using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;
using static Namotion.Devices.Sonos.Tests.Testing.TestFixtures;

namespace Namotion.Devices.Sonos.Tests;

public class SonosSystemTopologyTests
{
    private static SonosTopology WithoutKitchen(SonosTopology household) =>
        new(household.Groups.Where(group => group.CoordinatorUuid != TestFixtures.KitchenUuid).ToArray());

    [Fact]
    public void WhenCreated_ThenHasDefaults()
    {
        // Act
        var system = CreateSystem();

        // Assert
        Assert.Null(system.SeedHost);
        Assert.Null(system.EventCallbackHost);
        Assert.Equal(6329, system.EventPort);
        Assert.Equal(TimeSpan.FromSeconds(30), system.PollingInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), system.RetryInterval);
        Assert.Empty(system.Players);
        Assert.Empty(system.Groups);
        Assert.Empty(system.Favorites);
        Assert.Equal(HomeBlaze.Abstractions.ServiceStatus.Stopped, system.Status);
    }

    [Fact]
    public void WhenSameFavoritesAreSetAgain_ThenTheArrayIsNotReplaced()
    {
        // Arrange
        var system = CreateSystem();
        system.SetFavorites([new SonosFavorite("Radio", "x-sonosapi-stream:1", false, null) { Metadata = "<DIDL-Lite/>" }]);
        var first = system.Favorites;

        // Act
        system.SetFavorites([new SonosFavorite("Radio", "x-sonosapi-stream:1", false, null) { Metadata = "<DIDL-Lite/>" }]);

        // Assert
        Assert.Same(first, system.Favorites);
    }

    [Fact]
    public void WhenOnlyStoredMetadataChanges_ThenTheArrayIsReplaced()
    {
        // Arrange
        var system = CreateSystem();
        system.SetFavorites([new SonosFavorite("Radio", "x-sonosapi-stream:1", false, null) { Metadata = "<DIDL-Lite/>" }]);
        var first = system.Favorites;

        // Act
        system.SetFavorites([new SonosFavorite("Radio", "x-sonosapi-stream:1", false, null) { Metadata = "<DIDL-Lite>token</DIDL-Lite>" }]);

        // Assert
        Assert.NotSame(first, system.Favorites);
        Assert.Equal("<DIDL-Lite>token</DIDL-Lite>", system.FindFavorite("radio")!.Metadata);
    }

    [Fact]
    public void WhenTopologyApplied_ThenPlayersAreKeyedByUuid()
    {
        // Arrange
        var system = CreateSystem();

        // Act
        system.ApplyTopology(ReadHousehold());

        // Assert
        Assert.Equal(
            new[] { TestFixtures.LivingRoomUuid, TestFixtures.TerraceUuid, TestFixtures.KitchenUuid, TestFixtures.OfficeUuid },
            system.Players.Keys);
        Assert.Equal("Küche", system.Players[TestFixtures.KitchenUuid].RoomName);
        Assert.Equal("10.0.0.121", system.Players[TestFixtures.KitchenUuid].IpAddress);
        Assert.True(system.Players[TestFixtures.KitchenUuid].IsInTopology);
    }

    [Fact]
    public void WhenTopologyApplied_ThenSatellitesAreNestedUnderTheirPlayer()
    {
        // Arrange
        var system = CreateSystem();

        // Act
        system.ApplyTopology(ReadHousehold());

        // Assert
        Assert.Equal(3, system.Players[TestFixtures.LivingRoomUuid].Satellites.Count);
        Assert.Equal(3, system.Players[TestFixtures.OfficeUuid].Satellites.Count);
        Assert.Empty(system.Players[TestFixtures.KitchenUuid].Satellites);
        Assert.Equal(SonosSatelliteRole.Subwoofer, system.Players[TestFixtures.OfficeUuid].Satellites["RINCON_A0000000000801400"].Role);
    }

    [Fact]
    public void WhenTopologyApplied_ThenSatellitesTakeTheRoomOfTheirPlayerAndTitlesNameTheRole()
    {
        // Arrange
        var system = CreateSystem();

        // Act
        system.ApplyTopology(ReadHousehold());

        // Assert
        var satellites = system.Players[TestFixtures.LivingRoomUuid].Satellites;
        var subwoofer = satellites["RINCON_A0000000000201400"];
        Assert.Equal("Wohnzimmer", subwoofer.RoomName);
        Assert.Equal("Sonos (Wohnzimmer, subwoofer)", subwoofer.Title);
        Assert.Equal("Sonos (Wohnzimmer, rear left)", satellites["RINCON_A0000000000301400"].Title);
        Assert.Equal("Sonos (Wohnzimmer, rear right)", satellites["RINCON_A0000000000401400"].Title);
        Assert.Equal(3, satellites.Values.Select(satellite => satellite.Title).Distinct().Count());
    }

    [Fact]
    public void WhenTopologyAppliedTwice_ThenInstancesAreKept()
    {
        // Arrange
        var system = CreateHousehold();
        var players = system.Players;
        var groups = system.Groups;
        var livingRoom = system.Players[TestFixtures.LivingRoomUuid];
        var subwoofer = livingRoom.Satellites["RINCON_A0000000000201400"];

        // Act
        system.ApplyTopology(ReadHousehold());

        // Assert
        Assert.Same(players, system.Players);
        Assert.Same(groups, system.Groups);
        Assert.Same(livingRoom, system.Players[TestFixtures.LivingRoomUuid]);
        Assert.Same(subwoofer, livingRoom.Satellites["RINCON_A0000000000201400"]);
    }

    [Fact]
    public void WhenPlayerIsMissingFromTwoTopologiesInARow_ThenItStaysButIsOffline()
    {
        // Arrange
        var system = CreateSystem();
        var household = ReadHousehold();
        system.ApplyTopology(household);
        ReportAllReachable(system);
        var withoutKitchen = WithoutKitchen(household);

        // Act
        system.ApplyTopology(withoutKitchen);
        system.ApplyTopology(withoutKitchen);

        // Assert
        var kitchen = system.Players[TestFixtures.KitchenUuid];
        Assert.False(kitchen.IsConnected);
        Assert.False(system.Groups.ContainsKey(TestFixtures.KitchenUuid));
    }

    [Fact]
    public void WhenPlayerIsMissingFromOneTopologyOnly_ThenNothingIsTornDown()
    {
        // Arrange
        var system = CreateSystem();
        var household = ReadHousehold();
        system.ApplyTopology(household);
        ReportAllReachable(system);
        var groups = system.Groups;

        // Act
        system.ApplyTopology(WithoutKitchen(household));
        system.ApplyTopology(household);

        // Assert
        Assert.True(system.Players[TestFixtures.KitchenUuid].IsConnected);
        Assert.Same(groups, system.Groups);
    }

    [Fact]
    public void WhenAnUnchangedTopologyFollowsOneThatMissesAPlayer_ThenTheMissingPlayerNeedsTwoReadsAgain()
    {
        // Arrange
        var kitchen = (TestFixtures.KitchenUuid, "Küche", new Uri("http://10.0.0.121:1400/"));
        var office = (TestFixtures.OfficeUuid, "Büro", new Uri("http://10.0.0.116:1400/"));
        var household = SonosEventBodies.CreateStandaloneTopology(kitchen, office);
        var withoutKitchen = SonosEventBodies.CreateStandaloneTopology(office);
        var system = CreateSystem();
        system.ApplyPolledTopology(household, system.NextOrder());
        ReportAllReachable(system);

        // Act
        system.ApplyPolledTopology(withoutKitchen, system.NextOrder());
        system.ApplyPolledTopology(household, system.NextOrder());
        system.ApplyPolledTopology(withoutKitchen, system.NextOrder());

        // Assert
        Assert.True(system.Players[TestFixtures.KitchenUuid].IsConnected);
    }

    [Fact]
    public void WhenReadsThatDoNotConfirmMissingPlayersMissAPlayerTwice_ThenThePlayerStaysConnected()
    {
        // Arrange
        var kitchen = (TestFixtures.KitchenUuid, "Küche", new Uri("http://10.0.0.121:1400/"));
        var office = (TestFixtures.OfficeUuid, "Büro", new Uri("http://10.0.0.116:1400/"));
        var withoutKitchen = SonosEventBodies.CreateStandaloneTopology(office);
        var system = CreateSystem();
        system.ApplyPolledTopology(SonosEventBodies.CreateStandaloneTopology(kitchen, office), system.NextOrder());
        ReportAllReachable(system);

        // Act
        system.ApplyPolledTopology(withoutKitchen, system.NextOrder(), confirmsMissingPlayers: false);
        system.ApplyPolledTopology(withoutKitchen, system.NextOrder(), confirmsMissingPlayers: false);

        // Assert
        Assert.True(system.Players[TestFixtures.KitchenUuid].IsConnected);
        Assert.Equal(2, system.Groups.Count);
    }

    [Fact]
    public void WhenAReadThatDoesNotConfirmMissingPlayersFollowsAPollThatMissesAPlayer_ThenOnlyTheNextPollTakesThePlayerOffline()
    {
        // Arrange
        var kitchen = (TestFixtures.KitchenUuid, "Küche", new Uri("http://10.0.0.121:1400/"));
        var office = (TestFixtures.OfficeUuid, "Büro", new Uri("http://10.0.0.116:1400/"));
        var withoutKitchen = SonosEventBodies.CreateStandaloneTopology(office);
        var system = CreateSystem();
        system.ApplyPolledTopology(SonosEventBodies.CreateStandaloneTopology(kitchen, office), system.NextOrder());
        ReportAllReachable(system);
        system.ApplyPolledTopology(withoutKitchen, system.NextOrder());

        // Act
        system.ApplyPolledTopology(withoutKitchen, system.NextOrder(), confirmsMissingPlayers: false);
        var isConnectedAfterTheUnconfirmingRead = system.Players[TestFixtures.KitchenUuid].IsConnected;
        system.ApplyPolledTopology(withoutKitchen, system.NextOrder());

        // Assert
        Assert.True(isConnectedAfterTheUnconfirmingRead);
        Assert.False(system.Players[TestFixtures.KitchenUuid].IsConnected);
    }

    [Fact]
    public void WhenAReadThatDoesNotConfirmMissingPlayersShowsAChangeOfAllPlayers_ThenItIsApplied()
    {
        // Arrange
        var kitchen = (TestFixtures.KitchenUuid, "Küche", new Uri("http://10.0.0.121:1400/"));
        var office = (TestFixtures.OfficeUuid, "Büro", new Uri("http://10.0.0.116:1400/"));
        var system = CreateSystem();
        system.ApplyPolledTopology(SonosEventBodies.CreateStandaloneTopology(kitchen, office), system.NextOrder());

        // Act
        system.ApplyPolledTopology(SonosEventBodies.CreateGroupTopology(office, kitchen), system.NextOrder(), confirmsMissingPlayers: false);

        // Assert
        Assert.Equal(TestFixtures.OfficeUuid, system.Players[TestFixtures.KitchenUuid].GroupCoordinatorUuid);
        Assert.Single(system.Groups);
    }

    [Fact]
    public void WhenAnUnchangedTopologyEventArrivesDuringAPoll_ThenThePolledTopologyIsSkipped()
    {
        // Arrange
        var kitchen = (TestFixtures.KitchenUuid, "Küche", new Uri("http://10.0.0.121:1400/"));
        var office = (TestFixtures.OfficeUuid, "Büro", new Uri("http://10.0.0.116:1400/"));
        var household = SonosEventBodies.CreateStandaloneTopology(kitchen, office);
        var system = CreateSystem();
        system.ApplyTopologyEvent(household);
        var pollStartedAt = system.NextOrder();

        // Act
        system.ApplyTopologyEvent(household);
        system.ApplyPolledTopology(SonosEventBodies.CreateGroupTopology(office, kitchen), pollStartedAt);

        // Assert
        Assert.Equal(2, system.Groups.Count);
    }

    [Fact]
    public void WhenMissingPlayerReappears_ThenInstanceIsKeptAndConnected()
    {
        // Arrange
        var system = CreateSystem();
        var household = ReadHousehold();
        system.ApplyTopology(household);
        ReportAllReachable(system);
        var kitchen = system.Players[TestFixtures.KitchenUuid];
        system.ApplyTopology(WithoutKitchen(household));
        system.ApplyTopology(WithoutKitchen(household));

        // Act
        system.ApplyTopology(household);

        // Assert
        Assert.Same(kitchen, system.Players[TestFixtures.KitchenUuid]);
        Assert.True(kitchen.IsConnected);
        Assert.True(system.Groups.ContainsKey(TestFixtures.KitchenUuid));
    }

    [Fact]
    public void WhenPlayerIsFoundInTheTopology_ThenItIsNotConnectedUntilItsFirstPoll()
    {
        // Arrange
        var system = CreateSystem();

        // Act
        system.ApplyTopology(ReadHousehold());

        // Assert
        var kitchen = system.Players[TestFixtures.KitchenUuid];
        Assert.False(kitchen.IsConnected);
        kitchen.ReportPollSucceeded();
        Assert.True(kitchen.IsConnected);
    }

    [Fact]
    public void WhenSatelliteMissingFromTopology_ThenItStaysButIsOffline()
    {
        // Arrange
        var system = CreateSystem();
        var household = ReadHousehold();
        system.ApplyTopology(household);
        const string subwooferUuid = "RINCON_A0000000000201400";
        ReportAllReachable(system);
        var subwoofer = system.Players[TestFixtures.LivingRoomUuid].Satellites[subwooferUuid];
        var withoutSubwoofer = new SonosTopology(household.Groups
            .Select(group => group with
            {
                Players = group.Players
                    .Select(member => member with { Satellites = member.Satellites.Where(satellite => satellite.Uuid != subwooferUuid).ToArray() })
                    .ToArray()
            })
            .ToArray());

        // Act
        system.ApplyTopology(withoutSubwoofer);

        // Assert
        Assert.Same(subwoofer, system.Players[TestFixtures.LivingRoomUuid].Satellites[subwooferUuid]);
        Assert.False(subwoofer.IsConnected);
        Assert.True(system.Players[TestFixtures.LivingRoomUuid].Satellites["RINCON_A0000000000401400"].IsConnected);
    }

    [Fact]
    public void WhenCoordinatorIsHandedOver_ThenGroupIsKeyedByTheNewCoordinator()
    {
        // Arrange
        var system = CreateSystem();
        var household = ReadHousehold();
        var livingRoomGroup = household.Groups.Single(group => group.CoordinatorUuid == TestFixtures.LivingRoomUuid);
        var kitchenGroup = household.Groups.Single(group => group.CoordinatorUuid == TestFixtures.KitchenUuid);
        var others = household.Groups.Where(group => group != livingRoomGroup && group != kitchenGroup).ToArray();
        system.ApplyTopology(new SonosTopology([.. others, livingRoomGroup with { Players = [.. livingRoomGroup.Players, kitchenGroup.Players[0]] }]));
        var handedOver = livingRoomGroup with
        {
            CoordinatorUuid = TestFixtures.KitchenUuid,
            Players = [kitchenGroup.Players[0], .. livingRoomGroup.Players]
        };

        // Act
        system.ApplyTopology(new SonosTopology([.. others, handedOver]));

        // Assert
        Assert.False(system.Groups.ContainsKey(TestFixtures.LivingRoomUuid));
        var group = system.Groups[TestFixtures.KitchenUuid];
        Assert.Same(system.Players[TestFixtures.KitchenUuid], group.Coordinator);
        Assert.Equal("Küche + Wohnzimmer", group.Title);
        Assert.True(system.Players[TestFixtures.KitchenUuid].IsGroupCoordinator);
        Assert.False(system.Players[TestFixtures.LivingRoomUuid].IsGroupCoordinator);
    }

    [Fact]
    public void WhenTopologyApplied_ThenGroupsAreKeyedByCoordinator()
    {
        // Arrange
        var system = CreateSystem();

        // Act
        system.ApplyTopology(ReadHousehold());

        // Assert
        Assert.Equal(4, system.Groups.Count);
        var group = system.Groups[TestFixtures.LivingRoomUuid];
        Assert.Same(system.Players[TestFixtures.LivingRoomUuid], group.Coordinator);
        Assert.Equal("Wohnzimmer", group.Title);
    }

    [Fact]
    public void WhenRoomJoinsGroup_ThenGroupMembersTitleAndCoordinatorUpdate()
    {
        // Arrange
        var system = CreateSystem();
        var household = ReadHousehold();
        system.ApplyTopology(household);
        var kitchen = household.Groups.Single(group => group.CoordinatorUuid == TestFixtures.KitchenUuid).Players[0];
        var grouped = new SonosTopology(household.Groups
            .Where(group => group.CoordinatorUuid != TestFixtures.KitchenUuid)
            .Select(group => group.CoordinatorUuid == TestFixtures.LivingRoomUuid
                ? group with { Players = [.. group.Players, kitchen] }
                : group)
            .ToArray());

        // Act
        system.ApplyTopology(grouped);

        // Assert
        Assert.Equal(3, system.Groups.Count);
        var group = system.Groups[TestFixtures.LivingRoomUuid];
        Assert.Equal("Wohnzimmer + Küche", group.Title);
        Assert.Equal(TestFixtures.LivingRoomUuid, system.Players[TestFixtures.KitchenUuid].GroupCoordinatorUuid);
        Assert.False(system.Players[TestFixtures.KitchenUuid].IsGroupCoordinator);
        Assert.True(system.Players[TestFixtures.LivingRoomUuid].IsGroupCoordinator);
    }

    [Fact]
    public void WhenPortableInTopology_ThenBatteryIsRead()
    {
        // Arrange
        var system = CreateSystem();

        // Act
        system.ApplyTopology(ReadHousehold());

        // Assert
        Assert.Equal(1m, system.Players[TestFixtures.TerraceUuid].BatteryLevel);
        Assert.True(system.Players[TestFixtures.TerraceUuid].IsCharging);
        Assert.Null(system.Players[TestFixtures.KitchenUuid].BatteryLevel);
    }

    [Fact]
    public void WhenReplacedSpeakerSharesRoomName_ThenFindPlayerReturnsTheConnectedOne()
    {
        // Arrange
        var system = CreateSystem();
        var household = ReadHousehold();
        system.ApplyTopology(household);
        const string replacementUuid = "RINCON_A0000000000901400";
        var replacement = new SonosTopology(household.Groups
            .Select(group => group.CoordinatorUuid == TestFixtures.KitchenUuid
                ? group with
                {
                    Id = replacementUuid + ":1",
                    CoordinatorUuid = replacementUuid,
                    Players = [group.Players[0] with { Uuid = replacementUuid, BaseUri = new Uri("http://10.0.0.122:1400/") }]
                }
                : group)
            .ToArray());
        system.ApplyTopology(replacement);
        system.ApplyTopology(replacement);

        // Act
        var player = system.FindPlayer("Küche");

        // Assert
        Assert.Same(system.Players[replacementUuid], player);
        Assert.Same(system.Players[TestFixtures.KitchenUuid], system.FindPlayer(TestFixtures.KitchenUuid));
    }

    [Fact]
    public void WhenOnlyMissingPlayerHasRoomName_ThenFindPlayerFallsBackToIt()
    {
        // Arrange
        var system = CreateSystem();
        var household = ReadHousehold();
        system.ApplyTopology(household);
        system.ApplyTopology(new SonosTopology(household.Groups.Where(group => group.CoordinatorUuid != TestFixtures.KitchenUuid).ToArray()));

        // Act
        var player = system.FindPlayer("Küche");

        // Assert
        Assert.Same(system.Players[TestFixtures.KitchenUuid], player);
    }

    [Theory]
    [InlineData("küche")]
    [InlineData("RINCON_A0000000000601400")]
    public void WhenFindingPlayerByRoomOrUuid_ThenReturnsIt(string value)
    {
        // Arrange
        var system = CreateHousehold();

        // Act
        var player = system.FindPlayer(value);

        // Assert
        Assert.Same(system.Players[TestFixtures.KitchenUuid], player);
    }
}
