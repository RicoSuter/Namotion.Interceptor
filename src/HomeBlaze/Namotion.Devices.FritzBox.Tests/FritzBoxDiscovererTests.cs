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
}
