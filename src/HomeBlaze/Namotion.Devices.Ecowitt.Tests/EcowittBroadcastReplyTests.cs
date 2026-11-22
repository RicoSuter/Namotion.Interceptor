using System.Net;
using Xunit;

namespace Namotion.Devices.Ecowitt.Tests;

public class EcowittBroadcastReplyTests
{
    // The reply of a GW2000A with firmware V3.3.3 at 192.168.1.216, captured on a real network.
    internal const string Gw2000Reply = "ffff1200273c8a1f32e157c0a801d8afc817475732303030412d57494649453135372056332e332e33a1";

    private static byte[] WithChecksum(byte[] datagram)
    {
        datagram[^1] = EcowittBroadcastReply.ComputeChecksum(datagram.AsSpan(2, datagram.Length - 3));
        return datagram;
    }

    [Fact]
    public void WhenTheReplyOfAGatewayIsParsed_ThenItsIdentityIsRead()
    {
        // Arrange
        var datagram = Convert.FromHexString(Gw2000Reply);

        // Act
        var reply = EcowittBroadcastReply.TryParse(datagram);

        // Assert
        Assert.NotNull(reply);
        Assert.Equal("3C:8A:1F:32:E1:57", reply.MacAddress);
        Assert.Equal("GW2000A-WIFIE157 V3.3.3", reply.Name);
    }

    [Fact]
    public void WhenTheChecksumOfTheRequestIsComputed_ThenItMatchesTheRequest()
    {
        // Arrange
        var request = EcowittGatewayDiscoverer.BroadcastRequest;

        // Act
        var checksum = EcowittBroadcastReply.ComputeChecksum(request.AsSpan(2, request.Length - 3));

        // Assert
        Assert.Equal(request[^1], checksum);
    }

    [Fact]
    public void WhenTheGatewayAnnouncesAnEmptyName_ThenTheReplyHasAnEmptyName()
    {
        // Arrange
        var datagram = WithChecksum(Convert.FromHexString("ffff120010" + "3c8a1f32e157" + "c0a801d8" + "afc8" + "00" + "00"));

        // Act
        var reply = EcowittBroadcastReply.TryParse(datagram);

        // Assert
        Assert.NotNull(reply);
        Assert.Equal(string.Empty, reply.Name);
    }

    [Theory]
    [InlineData(0, 0xFE)] // header
    [InlineData(1, 0x00)] // header
    [InlineData(2, 0x13)] // command
    [InlineData(4, 0x26)] // size one short
    [InlineData(3, 0x01)] // size far too large
    [InlineData(17, 0x18)] // name length past the end
    [InlineData(17, 0x16)] // name length short of the end
    public void WhenAFieldIsWrong_ThenNoReplyIsRead(int index, byte value)
    {
        // Arrange
        var datagram = Convert.FromHexString(Gw2000Reply);
        datagram[index] = value;
        WithChecksum(datagram);

        // Act
        var reply = EcowittBroadcastReply.TryParse(datagram);

        // Assert
        Assert.Null(reply);
    }

    [Fact]
    public void WhenTheChecksumIsWrong_ThenNoReplyIsRead()
    {
        // Arrange
        var datagram = Convert.FromHexString(Gw2000Reply);
        datagram[^1]++;

        // Act
        var reply = EcowittBroadcastReply.TryParse(datagram);

        // Assert
        Assert.Null(reply);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(18)]
    [InlineData(41)]
    public void WhenTheDatagramIsCut_ThenNoReplyIsRead(int length)
    {
        // Arrange
        var datagram = Convert.FromHexString(Gw2000Reply).AsSpan(0, length).ToArray();

        // Act
        var reply = EcowittBroadcastReply.TryParse(datagram);

        // Assert
        Assert.Null(reply);
    }

    [Fact]
    public void WhenBytesFollowTheChecksum_ThenNoReplyIsRead()
    {
        // Arrange
        var datagram = new byte[43];
        Convert.FromHexString(Gw2000Reply).CopyTo(datagram, 0);

        // Act
        var reply = EcowittBroadcastReply.TryParse(datagram);

        // Assert
        Assert.Null(reply);
    }
}
