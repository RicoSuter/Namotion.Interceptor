using System.Net;
using HomeBlaze.Services.Discovery.MulticastDns;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class MulticastDnsServiceCollectorTests
{
    private static readonly IPAddress Sender = IPAddress.Parse("192.0.2.99");

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
            .AddAddress("hue.local", "192.0.2.20")), Sender);

        // Act
        var instances = collector.GetInstances("_hue._tcp");

        // Assert
        var instance = Assert.Single(instances);
        Assert.Equal("Philips Hue - 1A2B3C", instance.InstanceName);
        Assert.Equal("hue.local", instance.HostName);
        Assert.Equal([IPAddress.Parse("192.0.2.20")], instance.Addresses);
        Assert.Equal("001788fffe1a2b3c", instance.Properties["BRIDGEID"]);
        Assert.Equal("", instance.Properties["flag"]);
    }

    [Fact]
    public void WhenAnInstanceNamesNoAddress_ThenTheSenderOfItsAnswerIsItsAddress()
    {
        // Arrange
        var collector = new MulticastDnsServiceCollector();
        collector.Add(Parse(new DnsMessageBuilder()
            .AddPointer("_shelly._tcp.local", "shellyplus1pm-a8032ab12345._shelly._tcp.local")), Sender);

        // Act
        var instance = Assert.Single(collector.GetInstances("_shelly._tcp"));

        // Assert
        Assert.Equal([Sender], instance.Addresses);
        Assert.Null(instance.HostName);
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
        var instances = collector.GetInstances("_hue._tcp");

        // Assert
        Assert.Empty(instances);
    }

    [Fact]
    public void WhenRecordNamesDifferInCaseFromThePointerTarget_ThenTheyAreStillCombined()
    {
        // Arrange
        var collector = new MulticastDnsServiceCollector();
        collector.Add(Parse(new DnsMessageBuilder()
            .AddPointer("_hue._tcp.local", "Philips Hue - 1A2B3C._hue._tcp.local")
            .AddService("PHILIPS HUE - 1a2b3c._HUE._TCP.LOCAL", 443, "Hue.Local")
            .AddText("philips hue - 1a2b3c._hue._tcp.local", "bridgeid=abc")
            .AddAddress("HUE.local", "192.0.2.20")), Sender);

        // Act
        var instance = Assert.Single(collector.GetInstances("_hue._tcp"));

        // Assert
        Assert.Equal("abc", instance.Properties["bridgeid"]);
        Assert.Equal([IPAddress.Parse("192.0.2.20")], instance.Addresses);
    }

    [Fact]
    public void WhenAServiceRecordHasNoAddressRecord_ThenTheSenderIsItsAddress()
    {
        // Arrange
        var collector = new MulticastDnsServiceCollector();
        collector.Add(Parse(new DnsMessageBuilder()
            .AddPointer("_hue._tcp.local", "Bridge._hue._tcp.local")
            .AddService("Bridge._hue._tcp.local", 443, "hue.local")), Sender);

        // Act
        var instance = Assert.Single(collector.GetInstances("_hue._tcp"));

        // Assert
        Assert.Equal("hue.local", instance.HostName);
        Assert.Equal([Sender], instance.Addresses);
    }

    [Fact]
    public void WhenTwoSendersAnnounceTheSameInstance_ThenItIsOneInstance()
    {
        // Arrange
        var collector = new MulticastDnsServiceCollector();
        var message = Parse(new DnsMessageBuilder().AddPointer("_hue._tcp.local", "Bridge._hue._tcp.local"));
        collector.Add(message, Sender);
        collector.Add(message, IPAddress.Parse("192.0.2.98"));

        // Act
        var instances = collector.GetInstances("_hue._tcp");

        // Assert
        Assert.Single(instances);
    }

    [Fact]
    public void WhenMoreInstancesAreAnnouncedThanTheLimit_ThenOnlyTheFirst64AreReturned()
    {
        // Arrange
        var collector = new MulticastDnsServiceCollector();
        var records = Enumerable.Range(0, 65)
            .Select(index => (DnsRecord)new PointerRecord("_hue._tcp.local", $"Bridge {index}._hue._tcp.local"))
            .ToList();
        collector.Add(records, Sender);

        // Act
        var instances = collector.GetInstances("_hue._tcp");

        // Assert
        Assert.Equal(Enumerable.Range(0, 64).Select(index => $"Bridge {index}"), instances.Select(instance => instance.InstanceName));
    }

    [Fact]
    public void WhenMoreRecordsArriveThanTheLimit_ThenTheCollectorStopsAtTheLimit()
    {
        // Arrange: the names are short, so the records stay far below the limit of their size.
        var collector = new MulticastDnsServiceCollector();
        var records = Enumerable.Range(0, 4095)
            .Select(index => (DnsRecord)new PointerRecord("a.local", $"{index}.local"))
            .ToList();
        records.Add(new PointerRecord("_hue._tcp.local", "Kept._hue._tcp.local"));
        records.Add(new PointerRecord("_hue._tcp.local", "Dropped._hue._tcp.local"));

        // Act
        collector.Add(records, Sender);
        collector.Add([new PointerRecord("_hue._tcp.local", "AlsoDropped._hue._tcp.local")], Sender);

        // Assert
        var instance = Assert.Single(collector.GetInstances("_hue._tcp"));
        Assert.Equal("Kept", instance.InstanceName);
    }

    [Fact]
    public void WhenTheRecordsHoldMoreTextThanTheLimit_ThenTheCollectorDropsTheRecordThatDoesNotFitAndAllLaterOnes()
    {
        // Arrange: each text record holds 32 768 characters, which are 64 KiB, so a fourth does not fit into 256 KiB
        // beside the pointer.
        var collector = new MulticastDnsServiceCollector();
        var text = new string('a', 32768 - "Filler.local".Length);
        var fillers = Enumerable.Range(0, 3)
            .Select(_ => (DnsRecord)new TextRecord("Filler.local", [text]))
            .ToList();
        collector.Add(fillers, Sender);
        collector.Add([new PointerRecord("_hue._tcp.local", "Kept._hue._tcp.local")], Sender);

        // Act
        collector.Add(
        [
            new TextRecord("Filler.local", [text]),
            new PointerRecord("_hue._tcp.local", "Dropped._hue._tcp.local")
        ], Sender);
        collector.Add([new PointerRecord("_hue._tcp.local", "AlsoDropped._hue._tcp.local")], Sender);

        // Assert
        var instance = Assert.Single(collector.GetInstances("_hue._tcp"));
        Assert.Equal("Kept", instance.InstanceName);
    }
}
