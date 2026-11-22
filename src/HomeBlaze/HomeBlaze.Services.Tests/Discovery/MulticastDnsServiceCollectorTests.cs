using System.Net;
using HomeBlaze.Services.Discovery.MulticastDns;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class MulticastDnsServiceCollectorTests
{
    private static readonly IPAddress Sender = IPAddress.Parse("192.168.1.99");

    private static List<DnsRecord> Parse(DnsMessageBuilder builder)
    {
        Assert.True(DnsMessage.TryParse(builder.Build(), out var records));
        return records;
    }

    [Theory]
    [InlineData("_hue._tcp", "_hue._tcp.local")]
    [InlineData("_hue._tcp.local", "_hue._tcp.local")]
    [InlineData(" _hue._tcp.local. ", "_hue._tcp.local")]
    public void WhenAServiceTypeIsGiven_ThenItsNameEndsInLocal(string serviceType, string expected)
    {
        // Act
        var name = MulticastDnsServiceCollector.GetServiceName(serviceType);

        // Assert
        Assert.Equal(expected, name);
    }

    [Fact]
    public void WhenTheRecordsOfAnInstanceArrive_ThenTheyAreCombined()
    {
        // Arrange
        var collector = new MulticastDnsServiceCollector();
        collector.Add(Parse(new DnsMessageBuilder()
            .AddPointer("_hue._tcp.local", "Philips Hue - 1A2B3C._hue._tcp.local")
            .AddService("Philips Hue - 1A2B3C._hue._tcp.local", 443, "hue.local")
            .AddText("Philips Hue - 1A2B3C._hue._tcp.local", "bridgeid=001788fffe1a2b3c", "BridgeId=ignored", "flag")
            .AddAddress("hue.local", "192.168.1.20")), Sender);

        // Act
        var services = collector.GetServices("_hue._tcp");

        // Assert
        var service = Assert.Single(services);
        Assert.Equal("Philips Hue - 1A2B3C", service.InstanceName);
        Assert.Equal("hue.local", service.HostName);
        Assert.Equal(443, service.Port);
        Assert.Equal([IPAddress.Parse("192.168.1.20")], service.Addresses);
        Assert.Equal("001788fffe1a2b3c", service.Properties["BRIDGEID"]);
        Assert.Equal("", service.Properties["flag"]);
    }

    [Fact]
    public void WhenAnInstanceNamesNoAddress_ThenTheSenderOfItsAnswerIsItsAddress()
    {
        // Arrange
        var collector = new MulticastDnsServiceCollector();
        collector.Add(Parse(new DnsMessageBuilder()
            .AddPointer("_shelly._tcp.local", "shellyplus1pm-a8032ab12345._shelly._tcp.local")), Sender);

        // Act
        var service = Assert.Single(collector.GetServices("_shelly._tcp"));

        // Assert
        Assert.Equal([Sender], service.Addresses);
        Assert.Null(service.HostName);
    }

    [Fact]
    public void WhenAPointerBelongsToAnotherServiceType_ThenItIsIgnored()
    {
        // Arrange
        var collector = new MulticastDnsServiceCollector();
        collector.Add(Parse(new DnsMessageBuilder()
            .AddPointer("_http._tcp.local", "Printer._http._tcp.local")
            .AddPointer("_hue._tcp.local", "Printer._http._tcp.local")), Sender);

        // Act
        var services = collector.GetServices("_hue._tcp");

        // Assert
        Assert.Empty(services);
    }
}
