using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class ZoneGroupStateParserTests
{
    private static SonosTopology ParseHousehold() =>
        ZoneGroupStateParser.Parse(TestFixtures.Read("zone-group-state.xml"));

    [Fact]
    public void WhenParsingHousehold_ThenEachGroupHasItsVisiblePlayer()
    {
        // Act
        var topology = ParseHousehold();

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
    public void WhenParsingHomeTheater_ThenSatelliteRolesComeFromTheChannelMap()
    {
        // Act
        var livingRoom = ParseHousehold().Groups[0].Players[0];

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
        var topology = ParseHousehold();
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
        var terrace = ParseHousehold().Groups[1].Players[0];

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
}
