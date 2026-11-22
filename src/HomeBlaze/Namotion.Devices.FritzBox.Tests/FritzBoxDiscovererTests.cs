using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxDiscovererTests
{
    private static FritzBox CreateRouter(string? hostAddress, string? udn) =>
        new(NullLogger<FritzBox>.Instance) { HostAddress = hostAddress, Udn = udn };

    private static FritzBoxDiscoveryResult Found(string hostAddress, string? udn) =>
        new(hostAddress, "FRITZ!Box 7590", "FRITZ!Box 7590", udn, IsRepeater: false);

    [Fact]
    public void WhenTheUdnsMatch_ThenARouterAtAnotherAddressIsTheSame()
    {
        // Act & Assert
        Assert.True(FritzBoxDiscoverer.IsSameRouter(CreateRouter("192.168.178.1", "uuid:a"), Found("192.168.178.2", "UUID:A")));
    }

    [Fact]
    public void WhenTheHostAddressesMatch_ThenARouterWithAnotherUdnIsTheSame()
    {
        // Act & Assert
        Assert.True(FritzBoxDiscoverer.IsSameRouter(CreateRouter("192.168.178.1", "uuid:a"), Found("192.168.178.1", "uuid:b")));
    }

    [Fact]
    public void WhenNeitherTheUdnNorTheHostAddressMatches_ThenTheRouterIsAnother()
    {
        // Act & Assert
        Assert.False(FritzBoxDiscoverer.IsSameRouter(CreateRouter("192.168.178.1", "uuid:a"), Found("192.168.178.2", "uuid:b")));
        Assert.False(FritzBoxDiscoverer.IsSameRouter(CreateRouter("192.168.178.1", "uuid:a"), Found("192.168.178.2", null)));
    }

    [Fact]
    public void WhenTheRouterHasNoUdn_ThenTheHostAddressDecides()
    {
        // Act & Assert
        Assert.True(FritzBoxDiscoverer.IsSameRouter(CreateRouter(" Fritz.Box ", null), Found("fritz.box", "uuid:a")));
        Assert.False(FritzBoxDiscoverer.IsSameRouter(CreateRouter("192.168.178.1", null), Found("192.168.178.2", "uuid:a")));
    }

    [Fact]
    public void WhenAResultDescribesARepeater_ThenItsDetailSaysSo()
    {
        // Arrange
        var result = new FritzBoxDiscoveryResult("192.168.178.3", null, "FRITZ!Repeater 1200", "uuid:r", IsRepeater: true);

        // Act & Assert
        Assert.Equal("uuid:r", result.Key);
        Assert.Equal("FRITZ!Repeater 1200", result.DisplayName);
        Assert.Equal("192.168.178.3 (repeater)", result.Detail);
    }

    [Fact]
    public void WhenOneRouterAndRepeatersAreFound_ThenTheRepeatersAreIncludedItemsOfTheRouter()
    {
        // Arrange
        var router = Found("192.168.178.1", "uuid:router");
        FritzBoxDiscoveryResult[] results =
        [
            router,
            new("192.168.178.4", "Repeater Office", "FRITZ!Repeater 1200 AX", "uuid:office", IsRepeater: true),
            new("192.168.178.3", null, "FRITZ!Repeater 600", "uuid:attic", IsRepeater: true)
        ];

        // Act
        var included = FritzBoxDiscoverer.IncludeRepeaters(results);

        // Assert
        var single = Assert.Single(included);
        Assert.Equal(router.Key, single.Key);
        Assert.Equal("192.168.178.1", single.Detail);
        Assert.Equal(
            [new DiscoveredItem("FRITZ!Repeater 600", "192.168.178.3"), new DiscoveredItem("Repeater Office", "192.168.178.4")],
            single.IncludedItems);
    }

    [Fact]
    public void WhenTwoRoutersAreFound_ThenTheRepeatersStayResultsOfTheirOwn()
    {
        // Arrange
        FritzBoxDiscoveryResult[] results =
        [
            Found("192.168.178.1", "uuid:first"),
            Found("192.168.179.1", "uuid:second"),
            new("192.168.178.3", null, "FRITZ!Repeater 600", "uuid:repeater", IsRepeater: true)
        ];

        // Act
        var included = FritzBoxDiscoverer.IncludeRepeaters(results);

        // Assert
        Assert.Equal(results, included);
        Assert.All(included, result => Assert.Empty(result.IncludedItems));
    }

    [Fact]
    public void WhenOnlyARepeaterIsFound_ThenItStaysAResultOfItsOwn()
    {
        // Arrange
        FritzBoxDiscoveryResult[] results = [new("192.168.178.3", null, "FRITZ!Repeater 600", "uuid:repeater", IsRepeater: true)];

        // Act
        var included = FritzBoxDiscoverer.IncludeRepeaters(results);

        // Assert
        Assert.Equal(results, included);
    }

    [Fact]
    public void WhenOnlyARouterIsFound_ThenItIncludesNothing()
    {
        // Arrange
        FritzBoxDiscoveryResult[] results = [Found("192.168.178.1", "uuid:router")];

        // Act
        var included = FritzBoxDiscoverer.IncludeRepeaters(results);

        // Assert
        Assert.Empty(Assert.Single(included).IncludedItems);
    }
}
