using Namotion.Devices.Sonos.Client;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosDiscoveryTests
{
    [Theory]
    [InlineData("10.0.0.5", "http://10.0.0.5:1400/")]
    [InlineData(" 10.0.0.5 ", "http://10.0.0.5:1400/")]
    [InlineData("127.0.0.1:5000", "http://127.0.0.1:5000/")]
    [InlineData("sonos-kitchen.local", "http://sonos-kitchen.local:1400/")]
    public void WhenCreatingDeviceUri_ThenDefaultsToSonosPort(string host, string expected)
    {
        // Act
        var uri = SonosDiscovery.CreateDeviceUri(host);

        // Assert
        Assert.Equal(new Uri(expected), uri);
    }

    [Fact]
    public void WhenDetectingLocalAddressForLoopback_ThenReturnsLoopback()
    {
        // Act
        var address = SonosDiscovery.DetectLocalAddress("127.0.0.1");

        // Assert
        Assert.Equal("127.0.0.1", address);
    }
}
