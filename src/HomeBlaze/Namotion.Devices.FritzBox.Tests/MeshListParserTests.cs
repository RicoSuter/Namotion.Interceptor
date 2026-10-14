using System.Text;
using System.Text.Json;
using Namotion.Devices.FritzBox.Parsing;
using Namotion.Devices.FritzBox.Tests.Testing;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class MeshListParserTests
{
    [Fact]
    public void WhenTheMeshListIsParsed_ThenNodesInterfacesAndLinksAreRead()
    {
        // Arrange
        using var stream = Fixtures.Open("meshlist.json");

        // Act
        var meshList = MeshListParser.Parse(stream);

        // Assert
        Assert.Equal(5, meshList.Nodes!.Count);
        var router = meshList.Nodes[0];
        Assert.True(router.IsMeshed == true);
        Assert.Equal("master", router.MeshRole);
        Assert.Equal("FRITZ!Box 7590", router.Model);
        var link = router.Interfaces![2].Links![0];
        Assert.Equal("nl-1", link.Uid);
        Assert.Equal("ni-4", link.InterfaceUid2);
        Assert.Equal(866000, link.CurrentDataRateReceive);
    }

    [Fact]
    public void WhenANumberIsSentAsText_ThenItIsStillRead()
    {
        // Arrange
        using var stream = ToStream("""{"nodes":[{"uid":"n-1","node_interfaces":[{"uid":"ni-1","node_links":[{"uid":"nl-1","cur_data_rate_rx":"1000"}]}]}]}""");

        // Act
        var meshList = MeshListParser.Parse(stream);

        // Assert
        Assert.Equal(1000, meshList.Nodes![0].Interfaces![0].Links![0].CurrentDataRateReceive);
    }

    [Theory]
    [InlineData("""{"nodes":[{"uid":"n-1",""")]
    [InlineData("null")]
    [InlineData("")]
    public void WhenTheMeshListIsMalformed_ThenParsingThrowsJsonException(string json)
    {
        // Arrange
        using var stream = ToStream(json);

        // Act & Assert
        Assert.Throws<JsonException>(() => MeshListParser.Parse(stream));
    }

    [Fact]
    public void WhenTheMeshListNestsTooDeeply_ThenParsingThrowsJsonException()
    {
        // Arrange
        using var stream = ToStream("""{"nodes":[{"extra":""" + string.Concat(Enumerable.Repeat("[", 1000)) + string.Concat(Enumerable.Repeat("]", 1000)) + "}]}");

        // Act
        var exception = Assert.Throws<JsonException>(() => MeshListParser.Parse(stream));

        // Assert
        Assert.Contains("depth", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenAnUnknownPropertyNestsWithinTheDepthLimit_ThenParsingStillSucceeds()
    {
        // Arrange
        using var stream = ToStream("""{"nodes":[{"uid":"n-1","extra":""" + string.Concat(Enumerable.Repeat("[", 60)) + string.Concat(Enumerable.Repeat("]", 60)) + "}]}");

        // Act
        var meshList = MeshListParser.Parse(stream);

        // Assert
        Assert.Equal("n-1", meshList.Nodes![0].Uid);
    }

    [Fact]
    public void WhenIsMeshedIsNull_ThenTheNodeIsStillRead()
    {
        // Arrange
        using var stream = ToStream("""{"nodes":[{"uid":"n-1","is_meshed":null}]}""");

        // Act
        var meshList = MeshListParser.Parse(stream);

        // Assert
        Assert.Null(meshList.Nodes![0].IsMeshed);
    }

    private static MemoryStream ToStream(string json) => new(Encoding.UTF8.GetBytes(json));
}
