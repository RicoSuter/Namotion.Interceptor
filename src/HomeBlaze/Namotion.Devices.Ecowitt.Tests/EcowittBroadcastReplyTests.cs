using Xunit;

namespace Namotion.Devices.Ecowitt.Tests;

public class EcowittBroadcastReplyTests
{
    // A reply of a GW2000A with firmware V3.3.3 at 192.0.2.216, with the layout of a real reply.
    internal const string Gw2000Reply = "ffff12002702abcd000001c00002d8afc817475732303030412d57494649303030312056332e332e3305";

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
        Assert.Equal("02:AB:CD:00:00:01", reply.MacAddress);
        Assert.Equal("GW2000A-WIFI0001 V3.3.3", reply.Name);
    }

    [Fact]
    public void WhenTheChecksumOfTheRequestIsComputed_ThenItMatchesTheRequest()
    {
        // Arrange
        var request = EcowittGatewayDiscovery.BroadcastRequest;

        // Act
        var checksum = EcowittBroadcastReply.ComputeChecksum(request.AsSpan(2, request.Length - 3));

        // Assert
        Assert.Equal(request[^1], checksum);
    }

    [Fact]
    public void WhenTheGatewayAnnouncesAnEmptyName_ThenTheReplyHasAnEmptyName()
    {
        // Arrange
        var datagram = WithChecksum(Convert.FromHexString("ffff120010" + "02abcd000001" + "c00002d8" + "afc8" + "00" + "00"));

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
