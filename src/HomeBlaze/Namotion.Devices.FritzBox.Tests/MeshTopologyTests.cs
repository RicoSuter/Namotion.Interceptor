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

    private static MeshTopology BuildFromFixture()
    {
        using var stream = Fixtures.Open("meshlist.json");
        return MeshTopology.Build(MeshListParser.Parse(stream));
    }
}
