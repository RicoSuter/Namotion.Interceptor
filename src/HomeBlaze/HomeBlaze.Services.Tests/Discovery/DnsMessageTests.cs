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

    [Fact]
    public void WhenAResponseHoldsAQuestion_ThenTheRecordsAfterItAreRead()
    {
        // Arrange
        var message = new DnsMessageBuilder()
            .AddQuestion(DnsMessageBuilder.EncodeName("_hue._tcp.local"))
            .AddAddress("hue.local", "192.168.1.20")
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out var records);

        // Assert
        Assert.True(isParsed);
        Assert.Equal(new AddressRecord("hue.local", IPAddress.Parse("192.168.1.20")), Assert.Single(records));
    }

    [Fact]
    public void WhenARecordIsInTheAdditionalSection_ThenItIsRead()
    {
        // Arrange
        var message = new DnsMessageBuilder()
            .AddPointer("_hue._tcp.local", "Bridge._hue._tcp.local")
            .InAdditionalSection()
            .AddAddress("hue.local", "192.168.1.20")
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out var records);

        // Assert
        Assert.True(isParsed);
        Assert.Equal(2, records.Count);
        Assert.Equal(new AddressRecord("hue.local", IPAddress.Parse("192.168.1.20")), records[1]);
    }

    [Fact]
    public void WhenTheMessageIsShorterThanTheHeader_ThenItIsRejected()
    {
        // Act
        var isParsed = DnsMessage.TryParse(new byte[11], out var records);

        // Assert
        Assert.False(isParsed);
        Assert.Empty(records);
    }

    [Theory]
    [InlineData(0x40)]
    [InlineData(0x80)]
    public void WhenALabelUsesAReservedLengthType_ThenTheMessageIsRejected(byte lengthByte)
    {
        // Arrange
        var message = new DnsMessageBuilder()
            .AddRecord([lengthByte, (byte)'a', 0], DnsMessage.AddressType, [192, 168, 1, 21])
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out _);

        // Assert
        Assert.False(isParsed);
    }

    [Fact]
    public void WhenANamePointsPastTheEndOfTheMessage_ThenTheMessageIsRejected()
    {
        // Arrange
        var message = new DnsMessageBuilder()
            .AddRecord([0xC3, 0xE8], DnsMessage.AddressType, [192, 168, 1, 21])
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out _);

        // Assert
        Assert.False(isParsed);
    }

    [Fact]
    public void WhenTwoNamesPointToEachOther_ThenTheMessageIsRejected()
    {
        // Arrange - the record name at offset 12 points to offset 14, which points back to offset 12.
        var message = new DnsMessageBuilder()
            .AddRecord([0xC0, 0x0E, 0xC0, 0x0C], DnsMessage.AddressType, [192, 168, 1, 21])
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out _);

        // Assert
        Assert.False(isParsed);
    }

    [Fact]
    public void WhenAQuestionNameIsALoop_ThenTheMessageIsRejected()
    {
        // Arrange
        var message = new DnsMessageBuilder()
            .AddQuestion([0xC0, 0x0C])
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out _);

        // Assert
        Assert.False(isParsed);
    }

    [Fact]
    public void WhenANameIsLongerThanTheLimitOnTheWire_ThenTheMessageIsRejected()
    {
        // Arrange
        var label = new string('a', 63);
        var message = new DnsMessageBuilder()
            .AddAddress(string.Join('.', Enumerable.Repeat(label, 5)), "192.168.1.21")
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out _);

        // Assert
        Assert.False(isParsed);
    }

    [Fact]
    public void WhenAQuestionNameIsLongerThanTheLimitOnTheWire_ThenTheMessageIsRejected()
    {
        // Arrange
        var label = new string('a', 63);
        var message = new DnsMessageBuilder()
            .AddQuestion(DnsMessageBuilder.EncodeName(string.Join('.', Enumerable.Repeat(label, 5))))
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out _);

        // Assert
        Assert.False(isParsed);
    }

    [Fact]
    public void WhenTheMessageHoldsMoreRecordsThanTheLimit_ThenOnlyTheFirstOnesAreRead()
    {
        // Arrange
        var builder = new DnsMessageBuilder();
        for (var index = 0; index < 300; index++)
        {
            builder.AddAddress($"host{index}.local", "192.168.1.21");
        }

        // Act
        var isParsed = DnsMessage.TryParse(builder.Build(), out var records);

        // Assert
        Assert.True(isParsed);
        Assert.Equal(256, records.Count);
        Assert.Equal("host255.local", records[^1].Name);
    }

    [Fact]
    public void WhenAServiceRecordIsTooShort_ThenTheMessageIsRejected()
    {
        // Arrange
        var message = new DnsMessageBuilder()
            .AddRecord(DnsMessageBuilder.EncodeName("Bridge._hue._tcp.local"), DnsMessage.ServiceType, new byte[6])
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out _);

        // Assert
        Assert.False(isParsed);
    }

    [Fact]
    public void WhenTheQuestionCountIsOverTheLimit_ThenTheMessageIsRejected()
    {
        // Arrange
        var builder = new DnsMessageBuilder();
        for (var index = 0; index < 257; index++)
        {
            builder.AddQuestion([0]);
        }

        // Act
        var isParsed = DnsMessage.TryParse(builder.Build(), out _);

        // Assert
        Assert.False(isParsed);
    }

    [Fact]
    public void WhenTheQuestionCountIsAtTheLimit_ThenTheMessageIsAccepted()
    {
        // Arrange
        var builder = new DnsMessageBuilder();
        for (var index = 0; index < 256; index++)
        {
            builder.AddQuestion([0]);
        }

        builder.AddAddress("hue.local", "192.168.1.20");

        // Act
        var isParsed = DnsMessage.TryParse(builder.Build(), out var records);

        // Assert
        Assert.True(isParsed);
        Assert.Single(records);
    }

    [Fact]
    public void WhenANameIsShortInCharactersButLongerThanTheLimitOnTheWire_ThenTheMessageIsRejected()
    {
        // Arrange - 5 labels of 30 two-byte characters are 155 characters and 5 * 61 + 1 = 306 bytes on the wire.
        var label = new string('\u00e4', 30);
        var message = new DnsMessageBuilder()
            .AddAddress(string.Join('.', Enumerable.Repeat(label, 5)), "192.168.1.21")
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out _);

        // Assert
        Assert.False(isParsed);
    }

    [Fact]
    public void WhenTheOpcodeIsNotZero_ThenNoRecordsAreRead()
    {
        // Arrange
        var message = new DnsMessageBuilder()
            .AddAddress("hue.local", "192.168.1.20")
            .Build();
        message[2] |= 0x10;

        // Act
        var isParsed = DnsMessage.TryParse(message, out var records);

        // Assert
        Assert.True(isParsed);
        Assert.Empty(records);
    }

    [Fact]
    public void WhenTheResponseCodeIsNotZero_ThenNoRecordsAreRead()
    {
        // Arrange
        var message = new DnsMessageBuilder()
            .WithResponseCode(2)
            .AddAddress("hue.local", "192.168.1.20")
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out var records);

        // Assert
        Assert.True(isParsed);
        Assert.Empty(records);
    }

    [Fact]
    public void WhenARecordHasATimeToLiveOfZero_ThenItIsSkipped()
    {
        // Arrange
        var message = new DnsMessageBuilder()
            .AddRecord(DnsMessageBuilder.EncodeName("gone.local"), DnsMessage.AddressType, [192, 168, 1, 20], timeToLive: 0)
            .AddAddress("hue.local", "192.168.1.21")
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out var records);

        // Assert
        Assert.True(isParsed);
        Assert.Equal(new AddressRecord("hue.local", IPAddress.Parse("192.168.1.21")), Assert.Single(records));
    }

    [Fact]
    public void WhenAPointerTargetRunsPastItsRecordData_ThenTheMessageIsRejected()
    {
        // Arrange - the data is the single byte 0xC0, which with the first byte of the next record's name (12) forms a
        // valid pointer to the first name, so the target is only found by reading past the data.
        var message = new DnsMessageBuilder()
            .AddRecord(DnsMessageBuilder.EncodeName("_hue._tcp.local"), DnsMessage.PointerType, [0xC0])
            .AddRecord(DnsMessageBuilder.EncodeName("abcdefghijkl.local"), DnsMessage.AddressType, [192, 168, 1, 21])
            .Build();

        // Act
        var isParsed = DnsMessage.TryParse(message, out _);

        // Assert
        Assert.False(isParsed);
    }
}
