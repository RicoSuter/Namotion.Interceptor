using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class ZoneGroupStateParserTests
{
    [Fact]
    public void WhenParsingHousehold_ThenEachGroupHasItsVisiblePlayer()
    {
        // Act
        var topology = TestFixtures.ReadHousehold();

        // Assert
        Assert.Equal(4, topology.Groups.Count);
        Assert.All(topology.Groups, group => Assert.Single(group.Players));
        Assert.Equal(
            new[] { "Wohnzimmer", "Terasse", "Küche", "Büro" },
            topology.Groups.Select(group => group.Players[0].RoomName));
        Assert.Equal(TestFixtures.LivingRoomUuid, topology.Groups[0].CoordinatorUuid);
        Assert.Equal("RINCON_A0000000000101400:1010349259", topology.Groups[0].Id);
    }

    [Fact]
    public void WhenHouseholdHasAZoneBridge_ThenItIsNotAPlayer()
    {
        // Act
        var topology = TestFixtures.ReadHousehold();

        // Assert
        Assert.DoesNotContain(topology.Groups.SelectMany(group => group.Players), player => player.Uuid == "RINCON_A0000000000B01400");
        Assert.DoesNotContain(topology.Groups, group => group.CoordinatorUuid == "RINCON_A0000000000B01400");
    }

    [Fact]
    public void WhenParsingHomeTheater_ThenSatelliteRolesComeFromTheChannelMap()
    {
        // Act
        var livingRoom = TestFixtures.ReadHousehold().Groups[0].Players[0];

        // Assert
        Assert.Equal(3, livingRoom.Satellites.Count);
        Assert.Equal(SonosSatelliteRole.Subwoofer, livingRoom.Satellites.Single(satellite => satellite.Uuid == "RINCON_A0000000000201400").Role);
        Assert.Equal(SonosSatelliteRole.RearLeft, livingRoom.Satellites.Single(satellite => satellite.Uuid == "RINCON_A0000000000301400").Role);
        Assert.Equal(SonosSatelliteRole.RearRight, livingRoom.Satellites.Single(satellite => satellite.Uuid == "RINCON_A0000000000401400").Role);
        Assert.Equal(new Uri("http://10.0.0.120:1400/"), livingRoom.Satellites.Single(satellite => satellite.Uuid == "RINCON_A0000000000401400").BaseUri);
    }

    [Fact]
    public void WhenParsingMember_ThenBaseUriVersionAndWirelessAreRead()
    {
        // Act
        var topology = TestFixtures.ReadHousehold();
        var livingRoom = topology.Groups[0].Players[0];
        var kitchen = topology.Groups[2].Players[0];

        // Assert
        Assert.Equal(new Uri("http://10.0.0.101:1400/"), livingRoom.BaseUri);
        Assert.Equal("97.1-80312", livingRoom.SoftwareVersion);
        Assert.False(livingRoom.IsWireless);
        Assert.True(kitchen.IsWireless);
    }

    [Fact]
    public void WhenParsingPortable_ThenMoreInfoIsKept()
    {
        // Act
        var terrace = TestFixtures.ReadHousehold().Groups[1].Players[0];

        // Assert
        Assert.Contains("BattPct:100", terrace.MoreInfo);
    }

    [Fact]
    public void WhenParsingStereoPair_ThenInvisibleMemberBecomesStereoPartner()
    {
        // Arrange
        const string xml = """
            <ZoneGroupState><ZoneGroups>
              <ZoneGroup Coordinator="RINCON_B0000000000101400" ID="RINCON_B0000000000101400:1">
                <ZoneGroupMember UUID="RINCON_B0000000000101400" Location="http://10.0.0.50:1400/xml/device_description.xml" ZoneName="Bad" ChannelMapSet="RINCON_B0000000000101400:LF,LF;RINCON_B0000000000201400:RF,RF" EthLink="0" />
                <ZoneGroupMember UUID="RINCON_B0000000000201400" Location="http://10.0.0.51:1400/xml/device_description.xml" ZoneName="Bad" Invisible="1" ChannelMapSet="RINCON_B0000000000101400:LF,LF;RINCON_B0000000000201400:RF,RF" EthLink="0" />
              </ZoneGroup>
            </ZoneGroups></ZoneGroupState>
            """;

        // Act
        var topology = ZoneGroupStateParser.Parse(xml);

        // Assert
        var player = Assert.Single(Assert.Single(topology.Groups).Players);
        var partner = Assert.Single(player.Satellites);
        Assert.Equal("RINCON_B0000000000201400", partner.Uuid);
        Assert.Equal(SonosSatelliteRole.StereoPartner, partner.Role);
    }

    [Fact]
    public void WhenRootIsZoneGroups_ThenItIsParsed()
    {
        // Arrange
        const string xml = """
            <ZoneGroups>
              <ZoneGroup Coordinator="RINCON_C0000000000101400" ID="RINCON_C0000000000101400:1">
                <ZoneGroupMember UUID="RINCON_C0000000000101400" Location="http://10.0.0.60:1400/xml/device_description.xml" ZoneName="Garage" />
              </ZoneGroup>
            </ZoneGroups>
            """;

        // Act
        var topology = ZoneGroupStateParser.Parse(xml);

        // Assert
        Assert.Equal("Garage", Assert.Single(Assert.Single(topology.Groups).Players).RoomName);
        Assert.Null(topology.Groups[0].Players[0].IsWireless);
    }

    [Fact]
    public void WhenCoordinatorIsListedSecond_ThenPlayersKeepDocumentOrderAndCoordinatorIsTheSecond()
    {
        // Arrange
        const string xml = """
            <ZoneGroups>
              <ZoneGroup Coordinator="RINCON_D0000000000201400" ID="RINCON_D0000000000201400:1">
                <ZoneGroupMember UUID="RINCON_D0000000000101400" Location="http://10.0.0.70:1400/xml/device_description.xml" ZoneName="Flur" />
                <ZoneGroupMember UUID="RINCON_D0000000000201400" Location="http://10.0.0.71:1400/xml/device_description.xml" ZoneName="Diele" />
              </ZoneGroup>
            </ZoneGroups>
            """;

        // Act
        var group = Assert.Single(ZoneGroupStateParser.Parse(xml).Groups);

        // Assert
        Assert.Equal(new[] { "RINCON_D0000000000101400", "RINCON_D0000000000201400" }, group.Players.Select(player => player.Uuid));
        Assert.Equal("RINCON_D0000000000201400", group.CoordinatorUuid);
    }

    [Fact]
    public void WhenAllMembersAreInvisible_ThenGroupIsDropped()
    {
        // Arrange
        const string xml = """
            <ZoneGroups>
              <ZoneGroup Coordinator="RINCON_E0000000000101400" ID="RINCON_E0000000000101400:1">
                <ZoneGroupMember UUID="RINCON_E0000000000101400" Location="http://10.0.0.80:1400/xml/device_description.xml" ZoneName="Hidden" Invisible="1" />
              </ZoneGroup>
            </ZoneGroups>
            """;

        // Act
        var topology = ZoneGroupStateParser.Parse(xml);

        // Assert
        Assert.Empty(topology.Groups);
    }

    [Fact]
    public void WhenGroupHasNoCoordinatorAttribute_ThenCoordinatorIsTheFirstPlayer()
    {
        // Arrange
        const string xml = """
            <ZoneGroups>
              <ZoneGroup ID="RINCON_F0000000000101400:1">
                <ZoneGroupMember UUID="RINCON_F0000000000101400" Location="http://10.0.0.90:1400/xml/device_description.xml" ZoneName="Eins" />
                <ZoneGroupMember UUID="RINCON_F0000000000201400" Location="http://10.0.0.91:1400/xml/device_description.xml" ZoneName="Zwei" />
              </ZoneGroup>
            </ZoneGroups>
            """;

        // Act
        var group = Assert.Single(ZoneGroupStateParser.Parse(xml).Groups);

        // Assert
        Assert.Equal("RINCON_F0000000000101400", group.CoordinatorUuid);
    }

    [Fact]
    public void WhenMemberHasNoUuid_ThenParseThrowsFormatExceptionNamingTheAttribute()
    {
        // Arrange
        const string xml = """
            <ZoneGroups>
              <ZoneGroup Coordinator="RINCON_G0000000000101400" ID="RINCON_G0000000000101400:1">
                <ZoneGroupMember Location="http://10.0.0.95:1400/xml/device_description.xml" ZoneName="Anonym" />
              </ZoneGroup>
            </ZoneGroups>
            """;

        // Act & Assert
        var exception = Assert.Throws<FormatException>(() => ZoneGroupStateParser.Parse(xml));
        Assert.Contains("UUID", exception.Message);
    }

    [Fact]
    public void WhenStereoPairHasASubwoofer_ThenTheChannelMapGivesItsRole()
    {
        // Arrange
        const string channelMap = "RINCON_0000000000001400:LF,LF;RINCON_0000000000011400:RF,RF;RINCON_0000000000021400:SW,SW";
        var xml = CreateGroup(
            CreateMember(0, $"ChannelMapSet=\"{channelMap}\"") +
            CreateMember(1, "Invisible=\"1\"") +
            CreateMember(2, "Invisible=\"1\""));

        // Act
        var player = Assert.Single(Assert.Single(ZoneGroupStateParser.Parse(xml).Groups).Players);

        // Assert
        Assert.Equal(
            [(CreateUuid(1), SonosSatelliteRole.StereoPartner), (CreateUuid(2), SonosSatelliteRole.Subwoofer)],
            player.Satellites.Select(satellite => (satellite.Uuid, satellite.Role)));
    }

    [Fact]
    public void WhenChannelMapListsPartnersInAnotherOrderThanTheGroup_ThenSatellitesKeepTheOrderOfTheGroup()
    {
        // Arrange
        var channelMap = $"{CreateUuid(3)}:RF,RF;{CreateUuid(0)}:LF,LF;{CreateUuid(1)}:SW,SW";
        var xml = CreateGroup(
            CreateMember(1, "Invisible=\"1\"") +
            CreateMember(0, $"ChannelMapSet=\"{channelMap}\"") +
            CreateMember(2, "Invisible=\"1\"") +
            CreateMember(3, "Invisible=\"1\""));

        // Act
        var player = Assert.Single(Assert.Single(ZoneGroupStateParser.Parse(xml).Groups).Players);

        // Assert: the invisible member the map does not name is no satellite of the player.
        Assert.Equal(
            [(CreateUuid(1), SonosSatelliteRole.Subwoofer), (CreateUuid(3), SonosSatelliteRole.StereoPartner)],
            player.Satellites.Select(satellite => (satellite.Uuid, satellite.Role)));
    }

    [Theory]
    [InlineData("ChannelMapSet", "Invisible=\"1\"", SonosSatelliteRole.StereoPartner)]
    [InlineData("HTSatChanMapSet", null, SonosSatelliteRole.Other)]
    public void WhenChannelMapNamesAUnitTwice_ThenItsFirstEntryGivesTheRole(string mapAttribute, string? memberAttributes, SonosSatelliteRole expectedRole)
    {
        // Arrange: the unit is an invisible member for a stereo pair and a Satellite element for a home theater.
        var channelMap = $"{CreateUuid(0)}:LF,RF;{CreateUuid(1)}:XX;{CreateUuid(1)}:SW;{CreateUuid(2)}:RR;{CreateUuid(2)}:LR";
        var units = memberAttributes is null
            ? (Inside: CreateSatellite(1) + CreateSatellite(2), Beside: string.Empty)
            : (Inside: string.Empty, Beside: CreateMember(1, memberAttributes) + CreateMember(2, memberAttributes));
        var xml = CreateGroup(CreateMember(0, $"{mapAttribute}=\"{channelMap}\"", units.Inside) + units.Beside);

        // Act
        var player = Assert.Single(Assert.Single(ZoneGroupStateParser.Parse(xml).Groups).Players);

        // Assert
        Assert.Equal(
            [(CreateUuid(1), expectedRole), (CreateUuid(2), SonosSatelliteRole.RearRight)],
            player.Satellites.Select(satellite => (satellite.Uuid, satellite.Role)));
    }

    [Fact]
    public void WhenSatellitesShareAUuid_ThenEachIsKeptWithTheRoleOfTheChannelMap()
    {
        // Arrange
        var xml = CreateGroup(CreateMember(
            0,
            $"HTSatChanMapSet=\"{CreateUuid(0)}:LF,RF;{CreateUuid(1)}:SW\"",
            CreateSatellite(1) + CreateSatellite(2) + CreateSatellite(1)));

        // Act
        var player = Assert.Single(Assert.Single(ZoneGroupStateParser.Parse(xml).Groups).Players);

        // Assert
        Assert.Equal(
            [(CreateUuid(1), SonosSatelliteRole.Subwoofer), (CreateUuid(2), SonosSatelliteRole.Other), (CreateUuid(1), SonosSatelliteRole.Subwoofer)],
            player.Satellites.Select(satellite => (satellite.Uuid, satellite.Role)));
    }

    [Fact]
    public void WhenInvisibleMembersShareAUuid_ThenAPlayerGetsOnePartnerForItTheLastOfThem()
    {
        // Arrange: a real group never repeats a UUID. A partner per repeat and player would let a document of a few
        // thousand members yield millions of satellites.
        var xml = CreateGroup(
            CreateMember(0, $"ChannelMapSet=\"{CreateUuid(0)}:LF,LF;{CreateUuid(1)}:RF,RF\"") +
            CreateMember(1, "Invisible=\"1\" SoftwareVersion=\"1\"") +
            CreateMember(1, "Invisible=\"1\" SoftwareVersion=\"2\""));

        // Act
        var player = Assert.Single(Assert.Single(ZoneGroupStateParser.Parse(xml).Groups).Players);

        // Assert
        var partner = Assert.Single(player.Satellites);
        Assert.Equal(CreateUuid(1), partner.Uuid);
        Assert.Equal("2", partner.SoftwareVersion);
    }

    [Fact]
    public void WhenSeveralPlayersNameOneInvisibleMember_ThenOnlyTheFirstOfThemGetsItAsPartner()
    {
        // Arrange: a real member belongs to one room. A partner for every player that names it would read the member
        // once per player and let a document of n members yield n times n satellites.
        var attributes = $"ChannelMapSet=\"{CreateUuid(9)}:RF,RF\"";
        var xml = CreateGroup(
            CreateMember(0, attributes) +
            CreateMember(1, attributes) +
            CreateMember(9, "Invisible=\"1\"") +
            CreateMember(2, attributes));

        // Act
        var players = Assert.Single(ZoneGroupStateParser.Parse(xml).Groups).Players;

        // Assert
        Assert.Equal(CreateUuid(9), Assert.Single(players[0].Satellites).Uuid);
        Assert.Empty(players[1].Satellites);
        Assert.Empty(players[2].Satellites);
    }

    [Fact]
    public void WhenAnInvisibleMemberHasNoUuid_ThenParseThrowsOnlyForAGroupWithAPlayer()
    {
        // Arrange
        const string anonymous = """<ZoneGroupMember Location="http://10.0.0.95:1400/xml/device_description.xml" Invisible="1" />""";

        // Act
        var withoutPlayer = ZoneGroupStateParser.Parse(CreateGroup(anonymous));
        var exception = Record.Exception(() => ZoneGroupStateParser.Parse(CreateGroup(CreateMember(0, string.Empty) + anonymous)));

        // Assert
        Assert.Empty(withoutPlayer.Groups);
        Assert.Contains("UUID", Assert.IsType<FormatException>(exception).Message);
    }

    [Fact]
    public void WhenAPlayerNamesThousandsOfPartnersInItsChannelMap_ThenParsingStaysWithinTheAllocationLimit()
    {
        // Arrange
        const int partnerCount = 1000;
        var partners = Enumerable.Range(1, partnerCount).ToArray();
        var channelMap = string.Join(';', partners.Select(partner => $"{CreateUuid(partner)}:RF,RF"));
        var xml = CreateGroup(
            CreateMember(0, $"ChannelMapSet=\"{channelMap}\"") +
            string.Concat(partners.Select(partner => CreateMember(partner, "Invisible=\"1\""))));

        // Act
        var (topology, allocatedBytes) = ParseAndMeasure(xml);

        // Assert
        var player = Assert.Single(Assert.Single(topology.Groups).Players);
        Assert.Equal(partners.Select(CreateUuid), player.Satellites.Select(satellite => satellite.Uuid));
        Assert.All(player.Satellites, satellite => Assert.Equal(SonosSatelliteRole.StereoPartner, satellite.Role));
        Assert.InRange(allocatedBytes, 0, MaximumAllocatedBytes);
    }

    [Fact]
    public void WhenAHomeTheaterPlayerHasThousandsOfSatellites_ThenParsingStaysWithinTheAllocationLimit()
    {
        // Arrange
        const int satelliteCount = 1000;
        var satellites = Enumerable.Range(1, satelliteCount).ToArray();
        var channelMap = string.Join(';', satellites.Select(satellite => $"{CreateUuid(satellite)}:{(satellite % 2 == 0 ? "LR,LTR" : "SW")}"));
        var xml = CreateGroup(CreateMember(0, $"HTSatChanMapSet=\"{channelMap}\"", string.Concat(satellites.Select(CreateSatellite))));

        // Act
        var (topology, allocatedBytes) = ParseAndMeasure(xml);

        // Assert
        var player = Assert.Single(Assert.Single(topology.Groups).Players);
        Assert.Equal(
            satellites.Select(satellite => (CreateUuid(satellite), satellite % 2 == 0 ? SonosSatelliteRole.RearLeft : SonosSatelliteRole.Subwoofer)),
            player.Satellites.Select(satellite => (satellite.Uuid, satellite.Role)));
        Assert.InRange(allocatedBytes, 0, MaximumAllocatedBytes);
    }

    [Fact]
    public void WhenAGroupHasThousandsOfStereoPairs_ThenParsingStaysWithinTheAllocationLimit()
    {
        // Arrange: player 0 pairs with partner 1000, player 1 with partner 1001 and so on.
        const int pairCount = 1000;
        var players = Enumerable.Range(0, pairCount).ToArray();
        var xml = CreateGroup(
            string.Concat(players.Select(player =>
                CreateMember(player, $"ChannelMapSet=\"{CreateUuid(player)}:LF,LF;{CreateUuid(player + pairCount)}:RF,RF\""))) +
            string.Concat(players.Select(player => CreateMember(player + pairCount, "Invisible=\"1\""))));

        // Act
        var (topology, allocatedBytes) = ParseAndMeasure(xml);

        // Assert
        var group = Assert.Single(topology.Groups);
        Assert.Equal(
            players.Select(player => (CreateUuid(player), CreateUuid(player + pairCount))),
            group.Players.Select(player => (player.Uuid, Assert.Single(player.Satellites).Uuid)));
        Assert.InRange(allocatedBytes, 0, MaximumAllocatedBytes);
    }

    [Fact]
    public void WhenXmlIsMalformed_ThenParseThrows()
    {
        // Act & Assert
        Assert.ThrowsAny<System.Xml.XmlException>(() => ZoneGroupStateParser.Parse("<ZoneGroups><ZoneGroup>"));
    }

    // What parsing one of the large documents may allocate. One walk of each channel map takes 1 to 3 MB; splitting
    // the map for every satellite, or for every invisible member and player, takes 90 to 200 MB.
    private const long MaximumAllocatedBytes = 16 * 1024 * 1024;

    // Allocations are counted instead of time, which a slow machine stretches. They catch work that allocates per
    // step, as the split of a channel map does, and not a loop that only costs time.
    private static (SonosTopology Topology, long AllocatedBytes) ParseAndMeasure(string xml)
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var topology = ZoneGroupStateParser.Parse(xml);
        return (topology, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    private static string CreateUuid(int number) => $"RINCON_{number:X12}1400";

    private static string CreateGroup(string members) =>
        $"""<ZoneGroups><ZoneGroup Coordinator="{CreateUuid(0)}" ID="{CreateUuid(0)}:1">{members}</ZoneGroup></ZoneGroups>""";

    private static string CreateMember(int number, string attributes, string satellites = "") =>
        $"""<ZoneGroupMember UUID="{CreateUuid(number)}" Location="http://10.0.0.1:1400/xml/device_description.xml" {attributes}>{satellites}</ZoneGroupMember>""";

    private static string CreateSatellite(int number) =>
        $"""<Satellite UUID="{CreateUuid(number)}" Location="http://10.0.0.2:1400/xml/device_description.xml" Invisible="1" />""";
}
