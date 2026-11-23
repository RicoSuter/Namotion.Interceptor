using System.Net;
using Xunit;

namespace Namotion.Devices.MyStrom.Tests;

public class MyStromAnnouncementTests
{
    // The announcement of a WiFi Switch CH v2 (type 106) with status flags 0x06, with the layout of a real announcement.
    private const string SwitchAnnouncement = "02abcd0000106a06";

    private static readonly IPAddress SourceAddress = IPAddress.Parse("192.0.2.59");

    [Fact]
    public void WhenTheAnnouncementOfASwitchIsParsed_ThenItsIdentityIsRead()
    {
        // Arrange
        var datagram = Convert.FromHexString(SwitchAnnouncement);

        // Act
        var announcement = MyStromAnnouncement.TryParse(datagram, SourceAddress);

        // Assert
        Assert.NotNull(announcement);
        Assert.Equal("02ABCD000010", announcement.MacAddress);
        Assert.Equal("WS2", announcement.DeviceType);
        Assert.Equal(SourceAddress, announcement.IpAddress);
    }

    [Theory]
    [InlineData(0x6A, "WS2")]
    [InlineData(0x6B, "WSE")]
    public void WhenASupportedSwitchTypeIsAnnounced_ThenItsLiteralTypeIsRead(byte type, string deviceType)
    {
        // Arrange
        var datagram = Convert.FromHexString(SwitchAnnouncement);
        datagram[6] = type;

        // Act
        var announcement = MyStromAnnouncement.TryParse(datagram, SourceAddress);

        // Assert
        Assert.NotNull(announcement);
        Assert.Equal(deviceType, announcement.DeviceType);
    }

    [Theory]
    [InlineData(101)] // Switch CH v1
    [InlineData(102)] // Bulb
    [InlineData(103)] // Button plus 1st generation
    [InlineData(104)] // Button
    [InlineData(105)] // LED strip
    [InlineData(110)] // Motion sensor
    [InlineData(112)] // Gateway
    [InlineData(113)] // STECCO/CUBO
    [InlineData(118)] // Button Plus 2nd generation
    [InlineData(120)] // Switch Zero
    [InlineData(0)]
    [InlineData(255)]
    public void WhenAnUnsupportedTypeIsAnnounced_ThenNoAnnouncementIsRead(byte type)
    {
        // Arrange
        var datagram = Convert.FromHexString(SwitchAnnouncement);
        datagram[6] = type;

        // Act
        var announcement = MyStromAnnouncement.TryParse(datagram, SourceAddress);

        // Assert
        Assert.Null(announcement);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(16)]
    public void WhenTheDatagramIsNotEightBytesLong_ThenNoAnnouncementIsRead(int length)
    {
        // Arrange
        var datagram = new byte[length];
        Convert.FromHexString(SwitchAnnouncement).AsSpan(0, Math.Min(length, 8)).CopyTo(datagram);

        // Act
        var announcement = MyStromAnnouncement.TryParse(datagram, SourceAddress);

        // Assert
        Assert.Null(announcement);
    }
}
