using System.Buffers.Binary;
using System.Net;
using HomeBlaze.Services.Discovery.MulticastDns;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class DnsMessageTests
{
    [Fact]
    public void WhenAQueryIsCreated_ThenItAsksForThePointerRecordsOfTheName()
    {
        // Act
        var query = DnsMessage.CreatePointerQuery("_hue._tcp.local");

        // Assert
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)));
        Assert.Equal(DnsMessageBuilder.EncodeName("_hue._tcp.local"), query[12..^4]);
        Assert.Equal(DnsMessage.PointerType, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 2)));
    }

    [Fact]
    public void WhenAMessageIsAQuery_ThenItHoldsNoRecords()
    {
        // Act
        var isParsed = DnsMessage.TryParse(DnsMessage.CreatePointerQuery("_hue._tcp.local"), out var records);

        // Assert
        Assert.True(isParsed);
        Assert.Empty(records);
    }

    [Fact]
    public void WhenAnAnswerHoldsPointerServiceTextAndAddressRecords_ThenAllAreRead()
    {
        // Arrange
        var message = new DnsMessageBuilder()
            .AddPointer("_hue._tcp.local", "Bridge._hue._tcp.local")
            .AddService("Bridge._hue._tcp.local", 443, "hue.local")
            .AddText("Bridge._hue._tcp.local", "bridgeid=001788fffe1a2b3c", "modelid=BSB002")
            .AddAddress("hue.local", "192.168.1.20")
            .AddRecord(DnsMessageBuilder.EncodeName("hue.local"), 28, new byte[16]) // AAAA, skipped
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out var records);

        // Assert
        DnsRecord[] expected =
        [
            new PointerRecord("_hue._tcp.local", "Bridge._hue._tcp.local"),
            new ServiceRecord("Bridge._hue._tcp.local", 443, "hue.local"),
            new AddressRecord("hue.local", IPAddress.Parse("192.168.1.20"))
        ];
        Assert.True(isParsed);
        Assert.Equal(expected, records.Where(record => record is not TextRecord));
        var text = Assert.Single(records.OfType<TextRecord>());
        Assert.Equal(["bridgeid=001788fffe1a2b3c", "modelid=BSB002"], text.Entries);
    }

    [Fact]
    public void WhenANameIsCompressed_ThenItIsExpanded()
    {
        // Arrange - the name of the second record points to the name of the first, which starts at offset 12.
        var message = new DnsMessageBuilder()
            .AddPointer("_hue._tcp.local", "Bridge._hue._tcp.local")
            .AddRecord([0xC0, 0x0C], DnsMessage.AddressType, [192, 168, 1, 21])
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out var records);

        // Assert
        Assert.True(isParsed);
        Assert.Equal(new AddressRecord("_hue._tcp.local", IPAddress.Parse("192.168.1.21")), records[1]);
    }

    [Fact]
    public void WhenANamePointsToItself_ThenTheMessageIsRejected()
    {
        // Arrange - the first name, at offset 12, is a pointer to offset 12.
        var message = new DnsMessageBuilder()
            .AddRecord([0xC0, 0x0C], DnsMessage.AddressType, [192, 168, 1, 21])
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out _);

        // Assert
        Assert.False(isParsed);
    }

    [Fact]
    public void WhenTheMessageIsTruncated_ThenItIsRejected()
    {
        // Arrange
        var message = new DnsMessageBuilder()
            .AddService("Bridge._hue._tcp.local", 443, "hue.local")
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message[..^3], out _);

        // Assert
        Assert.False(isParsed);
    }
}
