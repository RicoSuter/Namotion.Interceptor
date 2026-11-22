using System.Net;
using HomeBlaze.Services.Discovery;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class NetworkAdaptersTests
{
    [Theory]
    [InlineData("192.168.1.216", "255.255.255.0", "192.168.1.255")]
    [InlineData("172.17.0.1", "255.255.0.0", "172.17.255.255")]
    [InlineData("10.0.0.5", "255.255.255.252", "10.0.0.7")]
    public void WhenTheSubnetHasHostAddresses_ThenItsBroadcastAddressIsComputed(string address, string mask, string expected)
    {
        // Act
        var broadcastAddress = NetworkAdapters.GetBroadcastAddress(IPAddress.Parse(address), IPAddress.Parse(mask));

        // Assert
        Assert.Equal(IPAddress.Parse(expected), broadcastAddress);
    }

    [Theory]
    [InlineData("10.8.0.2", "255.255.255.255")]
    [InlineData("10.8.0.2", "255.255.255.254")]
    [InlineData("fe80::1", "255.255.255.0")]
    public void WhenTheAddressIsOfAPointToPointLinkOrNotIPv4_ThenItHasNoBroadcastAddress(string address, string mask)
    {
        // Act
        var broadcastAddress = NetworkAdapters.GetBroadcastAddress(IPAddress.Parse(address), IPAddress.Parse(mask));

        // Assert
        Assert.Null(broadcastAddress);
    }
}
