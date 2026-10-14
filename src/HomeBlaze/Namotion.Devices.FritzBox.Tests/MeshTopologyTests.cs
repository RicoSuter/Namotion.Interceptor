using System.Text;
using Namotion.Devices.FritzBox.Parsing;
using Namotion.Devices.FritzBox.Tests.Testing;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class MeshTopologyTests
{
    [Fact]
    public void WhenTheTopologyIsBuilt_ThenEveryMeshNodeIsAnAccessPointWithItsUplink()
    {
        // Act
        var topology = BuildFromFixture();

        // Assert
        Assert.Equal(2, topology.AccessPoints.Count);
        var router = topology.AccessPoints.Single(accessPoint => accessPoint.MacAddress == "BB:BB:BB:00:00:01");
        Assert.True(router.IsMaster);
        Assert.True(router.IsConnected);
        Assert.Null(router.UplinkType);
        var repeater = topology.AccessPoints.Single(accessPoint => accessPoint.MacAddress == "BB:BB:BB:00:00:02");
        Assert.False(repeater.IsMaster);
        Assert.True(repeater.IsConnected);
        Assert.Equal("WLAN", repeater.UplinkType);
        Assert.Equal(866m, repeater.UplinkSpeed);
        Assert.Equal("FRITZ!Repeater 2400", repeater.Model);
        Assert.Contains("BB:BB:BB:00:00:12", repeater.MacAddresses);
    }

    [Fact]
    public void WhenTheTopologyIsBuilt_ThenConnectedClientsGetTheirAccessPointBandOrPortAndSpeed()
    {
        // Act
        var topology = BuildFromFixture();

        // Assert
        Assert.Equal(new ClientPosition("BB:BB:BB:00:00:02", "FRITZ!Repeater", "2.4GHz", null, 144m, true), topology.ClientPositions["AA:AA:AA:00:00:01"]);
        Assert.Equal(new ClientPosition("BB:BB:BB:00:00:01", "fritz.box", null, 1, 1000m, false), topology.ClientPositions["AA:AA:AA:00:00:02"]);
        Assert.False(topology.ClientPositions.ContainsKey("AA:AA:AA:00:00:03"));
    }

    [Fact]
    public void WhenTheTopologyIsBuilt_ThenEveryAddressOfAMeshNodeIsKnown()
    {
        // Act
        var topology = BuildFromFixture();

        // Assert
        Assert.Contains("BB:BB:BB:00:00:01", topology.MeshMacAddresses);
        Assert.Contains("BB:BB:BB:00:00:02", topology.MeshMacAddresses);
        Assert.Contains("BB:BB:BB:00:00:12", topology.MeshMacAddresses);
        Assert.DoesNotContain("AA:AA:AA:00:00:01", topology.MeshMacAddresses);
    }

    [Fact]
    public void WhenTheMeshListHasNoNodes_ThenTheTopologyIsEmpty()
    {
        // Act
        var topology = MeshTopology.Build(new MeshList());

        // Assert
        Assert.Empty(topology.AccessPoints);
        Assert.Empty(topology.ClientPositions);
        Assert.Empty(topology.MeshMacAddresses);
    }

    [Theory]
    [InlineData("AP:2G:0", "2.4GHz")]
    [InlineData("AP:5G:0", "5GHz")]
    [InlineData("AP:6G:0", "6GHz")]
    [InlineData("AP_GUEST:2G:0", "2.4GHz")]
    [InlineData("LAN:1", null)]
    [InlineData(null, null)]
    public void WhenAnInterfaceNameIsRead_ThenItsBandIsKnown(string? interfaceName, string? expected)
    {
        // Act
        var band = MeshTopology.GetBand(interfaceName);

        // Assert
        Assert.Equal(expected, band);
    }

    [Theory]
    [InlineData("LAN:1", 1)]
    [InlineData("LAN:4", 4)]
    [InlineData("LAN", null)]
    [InlineData("LAN:x", null)]
    [InlineData(null, null)]
    public void WhenAnInterfaceNameIsRead_ThenItsPortIsKnown(string? interfaceName, int? expected)
    {
        // Act
        var port = MeshTopology.GetPort(interfaceName);

        // Assert
        Assert.Equal(expected, port);
    }

    [Fact]
    public void WhenTheMasterReportsIsMeshedFalse_ThenItIsStillAnAccessPointWithItsClient()
    {
        // Arrange
        const string json =
            """
            {
              "nodes": [
                { "uid": "n-1", "device_mac_address": "BB:BB:BB:00:00:01", "is_meshed": false, "mesh_role": "master",
                  "node_interfaces": [ { "uid": "ni-1", "name": "LAN:1", "type": "LAN", "mac_address": "BB:BB:BB:00:00:01",
                    "node_links": [ { "uid": "nl-1", "type": "LAN", "state": "CONNECTED", "node_interface_1_uid": "ni-1", "node_interface_2_uid": "ni-2", "cur_data_rate_rx": 1000000, "cur_data_rate_tx": 1000000 } ] } ] },
                { "uid": "n-2", "device_mac_address": "AA:AA:AA:00:00:01", "is_meshed": false, "mesh_role": "unknown",
                  "node_interfaces": [ { "uid": "ni-2", "name": "", "type": "LAN", "mac_address": "AA:AA:AA:00:00:01",
                    "node_links": [ { "uid": "nl-1", "type": "LAN", "state": "CONNECTED", "node_interface_1_uid": "ni-1", "node_interface_2_uid": "ni-2", "cur_data_rate_rx": 1000000, "cur_data_rate_tx": 1000000 } ] } ] }
              ]
            }
            """;
        using var stream = ToStream(json);

        // Act
        var topology = MeshTopology.Build(MeshListParser.Parse(stream));

        // Assert
        var router = Assert.Single(topology.AccessPoints);
        Assert.Equal("BB:BB:BB:00:00:01", router.MacAddress);
        Assert.True(router.IsMaster);
        Assert.True(router.IsConnected);
        Assert.True(topology.ClientPositions.ContainsKey("AA:AA:AA:00:00:01"));
    }

    [Fact]
    public void WhenANonMasterNodeHasSeveralMeshLinks_ThenItsUplinkIsChosenByPriorityNotByOrder()
    {
        // Arrange: a daisy chain master -> A -> B -> C. B's own node_interfaces list its downlink to C before its
        // uplink to A, and B's link to C is inserted into the document before its link to A, so a naive
        // "first link found" selection would wrongly pick the B-C link as B's uplink.
        const string json =
            """
            {
              "nodes": [
                { "uid": "n-master", "device_mac_address": "AA:AA:AA:00:00:00", "is_meshed": true, "mesh_role": "master",
                  "node_interfaces": [ { "uid": "i-master", "name": "LAN:1", "type": "WLAN", "mac_address": "AA:AA:AA:00:00:00",
                    "node_links": [ { "uid": "l-ma", "type": "WLAN", "state": "CONNECTED", "node_interface_1_uid": "i-master", "node_interface_2_uid": "i-a-up", "cur_data_rate_rx": 1000000, "cur_data_rate_tx": 1000000 } ] } ] },
                { "uid": "n-c", "device_mac_address": "AA:AA:AA:00:00:03", "is_meshed": true, "mesh_role": "slave",
                  "node_interfaces": [ { "uid": "i-c-up", "name": "UPLINK:5G:0", "type": "WLAN", "mac_address": "AA:AA:AA:00:00:03",
                    "node_links": [ { "uid": "l-bc", "type": "WLAN", "state": "CONNECTED", "node_interface_1_uid": "i-b-down", "node_interface_2_uid": "i-c-up", "cur_data_rate_rx": 100000, "cur_data_rate_tx": 100000 } ] } ] },
                { "uid": "n-b", "device_mac_address": "AA:AA:AA:00:00:02", "is_meshed": true, "mesh_role": "slave",
                  "node_interfaces": [
                    { "uid": "i-b-down", "name": "AP:5G:0", "type": "WLAN", "mac_address": "AA:AA:AA:00:00:02",
                      "node_links": [ { "uid": "l-bc", "type": "WLAN", "state": "CONNECTED", "node_interface_1_uid": "i-b-down", "node_interface_2_uid": "i-c-up", "cur_data_rate_rx": 100000, "cur_data_rate_tx": 100000 } ] },
                    { "uid": "i-b-up", "name": "UPLINK:5G:0", "type": "WLAN", "mac_address": "AA:AA:AA:00:00:02",
                      "node_links": [ { "uid": "l-ab", "type": "WLAN", "state": "CONNECTED", "node_interface_1_uid": "i-a-down", "node_interface_2_uid": "i-b-up", "cur_data_rate_rx": 500000, "cur_data_rate_tx": 500000 } ] } ] },
                { "uid": "n-a", "device_mac_address": "AA:AA:AA:00:00:01", "is_meshed": true, "mesh_role": "slave",
                  "node_interfaces": [
                    { "uid": "i-a-up", "name": "UPLINK:5G:0", "type": "WLAN", "mac_address": "AA:AA:AA:00:00:01",
                      "node_links": [ { "uid": "l-ma", "type": "WLAN", "state": "CONNECTED", "node_interface_1_uid": "i-master", "node_interface_2_uid": "i-a-up", "cur_data_rate_rx": 1000000, "cur_data_rate_tx": 1000000 } ] },
                    { "uid": "i-a-down", "name": "AP:5G:0", "type": "WLAN", "mac_address": "AA:AA:AA:00:00:01",
                      "node_links": [ { "uid": "l-ab", "type": "WLAN", "state": "CONNECTED", "node_interface_1_uid": "i-a-down", "node_interface_2_uid": "i-b-up", "cur_data_rate_rx": 500000, "cur_data_rate_tx": 500000 } ] } ] }
              ]
            }
            """;
        using var stream = ToStream(json);

        // Act
        var topology = MeshTopology.Build(MeshListParser.Parse(stream));

        // Assert
        var nodeA = topology.AccessPoints.Single(accessPoint => accessPoint.MacAddress == "AA:AA:AA:00:00:01");
        var nodeB = topology.AccessPoints.Single(accessPoint => accessPoint.MacAddress == "AA:AA:AA:00:00:02");
        Assert.Equal(1000m, nodeA.UplinkSpeed);
        Assert.Equal(500m, nodeB.UplinkSpeed);
    }

    [Fact]
    public void WhenAClientInterfaceHasNoMacAddress_ThenItsPositionKeyFallsBackToTheNodeMacAddress()
    {
        // Arrange
        const string json =
            """
            {
              "nodes": [
                { "uid": "n-1", "device_mac_address": "BB:BB:BB:00:00:01", "is_meshed": true, "mesh_role": "master",
                  "node_interfaces": [ { "uid": "ni-1", "name": "LAN:1", "type": "LAN", "mac_address": "BB:BB:BB:00:00:01",
                    "node_links": [ { "uid": "nl-1", "type": "LAN", "state": "CONNECTED", "node_interface_1_uid": "ni-1", "node_interface_2_uid": "ni-2", "cur_data_rate_rx": 1000000, "cur_data_rate_tx": 1000000 } ] } ] },
                { "uid": "n-2", "device_mac_address": "AA:AA:AA:00:00:09", "is_meshed": false, "mesh_role": "unknown",
                  "node_interfaces": [ { "uid": "ni-2", "name": "", "type": "LAN",
                    "node_links": [ { "uid": "nl-1", "type": "LAN", "state": "CONNECTED", "node_interface_1_uid": "ni-1", "node_interface_2_uid": "ni-2", "cur_data_rate_rx": 1000000, "cur_data_rate_tx": 1000000 } ] } ] }
              ]
            }
            """;
        using var stream = ToStream(json);

        // Act
        var topology = MeshTopology.Build(MeshListParser.Parse(stream));

        // Assert
        Assert.True(topology.ClientPositions.ContainsKey("AA:AA:AA:00:00:09"));
    }

    [Fact]
    public void WhenARepeaterHasNoConnectedLink_ThenItIsNotConnectedAndHasNoUplink()
    {
        // Arrange
        const string json =
            """
            {
              "nodes": [
                { "uid": "n-1", "device_mac_address": "BB:BB:BB:00:00:01", "is_meshed": true, "mesh_role": "master",
                  "node_interfaces": [ { "uid": "ni-1", "name": "AP:5G:0", "type": "WLAN", "mac_address": "BB:BB:BB:00:00:01",
                    "node_links": [ { "uid": "nl-1", "type": "WLAN", "state": "DISCONNECTED", "node_interface_1_uid": "ni-1", "node_interface_2_uid": "ni-2", "cur_data_rate_rx": 0, "cur_data_rate_tx": 0 } ] } ] },
                { "uid": "n-2", "device_mac_address": "BB:BB:BB:00:00:02", "is_meshed": true, "mesh_role": "slave",
                  "node_interfaces": [ { "uid": "ni-2", "name": "UPLINK:5G:0", "type": "WLAN", "mac_address": "BB:BB:BB:00:00:02",
                    "node_links": [ { "uid": "nl-1", "type": "WLAN", "state": "DISCONNECTED", "node_interface_1_uid": "ni-1", "node_interface_2_uid": "ni-2", "cur_data_rate_rx": 0, "cur_data_rate_tx": 0 } ] } ] }
              ]
            }
            """;
        using var stream = ToStream(json);

        // Act
        var topology = MeshTopology.Build(MeshListParser.Parse(stream));

        // Assert
        var repeater = topology.AccessPoints.Single(accessPoint => accessPoint.MacAddress == "BB:BB:BB:00:00:02");
        Assert.False(repeater.IsConnected);
        Assert.Null(repeater.UplinkType);
        Assert.Null(repeater.UplinkSpeed);
    }

    [Fact]
    public void WhenAClientIsWiredToARepeatersLanPort_ThenItsPositionNamesThePortNotABand()
    {
        // Arrange
        const string json =
            """
            {
              "nodes": [
                { "uid": "n-1", "device_name": "fritz.box", "device_mac_address": "BB:BB:BB:00:00:01", "is_meshed": true, "mesh_role": "master",
                  "node_interfaces": [ { "uid": "ni-1", "name": "AP:5G:0", "type": "WLAN", "mac_address": "BB:BB:BB:00:00:01",
                    "node_links": [ { "uid": "nl-1", "type": "WLAN", "state": "CONNECTED", "node_interface_1_uid": "ni-1", "node_interface_2_uid": "ni-2", "cur_data_rate_rx": 866000, "cur_data_rate_tx": 780000 } ] } ] },
                { "uid": "n-2", "device_name": "FRITZ!Repeater", "device_mac_address": "BB:BB:BB:00:00:02", "is_meshed": true, "mesh_role": "slave",
                  "node_interfaces": [
                    { "uid": "ni-2", "name": "UPLINK:5G:0", "type": "WLAN", "mac_address": "BB:BB:BB:00:00:02",
                      "node_links": [ { "uid": "nl-1", "type": "WLAN", "state": "CONNECTED", "node_interface_1_uid": "ni-1", "node_interface_2_uid": "ni-2", "cur_data_rate_rx": 866000, "cur_data_rate_tx": 780000 } ] },
                    { "uid": "ni-3", "name": "LAN:1", "type": "LAN", "mac_address": "BB:BB:BB:00:00:22",
                      "node_links": [ { "uid": "nl-2", "type": "LAN", "state": "CONNECTED", "node_interface_1_uid": "ni-3", "node_interface_2_uid": "ni-4", "cur_data_rate_rx": 1000000, "cur_data_rate_tx": 1000000 } ] } ] },
                { "uid": "n-3", "device_name": "printer", "device_mac_address": "AA:AA:AA:00:00:09", "is_meshed": false, "mesh_role": "unknown",
                  "node_interfaces": [ { "uid": "ni-4", "name": "", "type": "LAN", "mac_address": "AA:AA:AA:00:00:09",
                    "node_links": [ { "uid": "nl-2", "type": "LAN", "state": "CONNECTED", "node_interface_1_uid": "ni-3", "node_interface_2_uid": "ni-4", "cur_data_rate_rx": 1000000, "cur_data_rate_tx": 1000000 } ] } ] }
              ]
            }
            """;
        using var stream = ToStream(json);

        // Act
        var topology = MeshTopology.Build(MeshListParser.Parse(stream));

        // Assert
        var position = topology.ClientPositions["AA:AA:AA:00:00:09"];
        Assert.Equal("BB:BB:BB:00:00:02", position.AccessPointMacAddress);
        Assert.Equal("FRITZ!Repeater", position.AccessPointName);
        Assert.Null(position.Band);
        Assert.Equal(1, position.Port);
        Assert.Equal(1000m, position.LinkSpeed);
        Assert.False(position.IsWireless);
    }

    private static MeshTopology BuildFromFixture()
    {
        using var stream = Fixtures.Open("meshlist.json");
        return MeshTopology.Build(MeshListParser.Parse(stream));
    }

    private static MemoryStream ToStream(string json) => new(Encoding.UTF8.GetBytes(json));
}
