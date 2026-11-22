using Namotion.Devices.Sonos.Client;
using Rssdp;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosDiscoveryTests
{
    private const string ZonePlayer = "urn:schemas-upnp-org:device:ZonePlayer:1";

    [Theory]
    [InlineData("10.0.0.5", "http://10.0.0.5:1400/")]
    [InlineData(" 10.0.0.5 ", "http://10.0.0.5:1400/")]
    [InlineData("127.0.0.1:5000", "http://127.0.0.1:5000/")]
    [InlineData("10.0.0.5:80", "http://10.0.0.5:80/")]
    [InlineData("sonos-kitchen.local", "http://sonos-kitchen.local:1400/")]
    [InlineData("::1", "http://[::1]:1400/")]
    [InlineData("[::1]", "http://[::1]:1400/")]
    [InlineData("[::1]:80", "http://[::1]:80/")]
    public void WhenCreatingDeviceUri_ThenUsesExplicitPortOrSonosDefault(string host, string expected)
    {
        // Act
        var uri = SonosDiscovery.CreateDeviceUri(host);

        // Assert
        Assert.Equal(new Uri(expected), uri);
        Assert.Equal(new Uri(expected).Port, uri.Port);
    }

    [Theory]
    [InlineData("http://10.0.0.5")]
    [InlineData("")]
    [InlineData("10.0.0.5:")]
    [InlineData("10.0.0.5:abc")]
    [InlineData("10.0.0.5:0")]
    [InlineData("10.0.0.5:65536")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    [InlineData("bad host")]
    public void WhenCreatingDeviceUriFromInvalidHost_ThenThrows(string host)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => SonosDiscovery.CreateDeviceUri(host));
    }

    [Fact]
    public async Task WhenDetectingLocalAddressForLoopback_ThenReturnsLoopback()
    {
        // Act
        var address = await SonosDiscovery.DetectLocalAddressAsync("127.0.0.1", CancellationToken.None);

        // Assert
        Assert.Equal("127.0.0.1", address);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public async Task WhenDetectingLocalAddressForIpv6_ThenReturnsNull(string host)
    {
        // Act
        var address = await SonosDiscovery.DetectLocalAddressAsync(host, CancellationToken.None);

        // Assert
        Assert.Null(address);
    }

    [Theory]
    [InlineData("uuid:RINCON_A0000000000601400::" + ZonePlayer, "RINCON_A0000000000601400")]
    [InlineData("uuid:RINCON_A0000000000601400", "RINCON_A0000000000601400")]
    [InlineData("uuid:RINCON_::" + ZonePlayer, null)]
    [InlineData("uuid:2f402f80-da50-11e1-9b23-001788255acc::" + ZonePlayer, null)]
    [InlineData("uuid:RINCON_000e58a0123401400::" + ZonePlayer, "RINCON_000e58a0123401400")]
    [InlineData("uuid:RINCON_A000000000060140G::" + ZonePlayer, null)]
    [InlineData("uuid:RINCON_A0000/../601400::" + ZonePlayer, null)]
    [InlineData("uuid:RINCON_A00000000006014000000000000000000::" + ZonePlayer, null)]
    [InlineData("uuid:RINCON_A0000000000601400000000000000000::" + ZonePlayer, "RINCON_A0000000000601400000000000000000")]
    [InlineData(null, null)]
    public void WhenReadingTheSpeakerIdOfAUsn_ThenOnlySonosSpeakersHaveOne(string? usn, string? expected)
    {
        // Act
        var speakerId = SonosDiscovery.GetSpeakerId(usn);

        // Assert
        Assert.Equal(expected, speakerId);
    }

    [Theory]
    [InlineData(ZonePlayer, "uuid:RINCON_A0000000000601400::" + ZonePlayer, true, true)]
    [InlineData("upnp:rootdevice", "uuid:RINCON_A0000000000601400::upnp:rootdevice", true, false)]
    [InlineData(ZonePlayer, "uuid:2f402f80-da50-11e1-9b23-001788255acc::" + ZonePlayer, true, false)]
    [InlineData(ZonePlayer, null, true, false)]
    [InlineData(ZonePlayer, "uuid:RINCON_NOT-HEX::" + ZonePlayer, true, false)]
    [InlineData(ZonePlayer, "uuid:RINCON_A0000000000601400::" + ZonePlayer, false, false)]
    public void WhenFilteringDiscoveredDevices_ThenAcceptsOnlySonosZonePlayers(string notificationType, string? usn, bool hasLocation, bool expected)
    {
        // Arrange
        using var response = new HttpResponseMessage();
        var device = new DiscoveredSsdpDevice(notificationType, response.Headers)
        {
            Usn = usn,
            DescriptionLocation = hasLocation ? new Uri("http://10.0.0.5:1400/xml/device_description.xml") : null
        };

        // Act
        var isZonePlayer = SonosDiscovery.IsZonePlayer(device);

        // Assert
        Assert.Equal(expected, isZonePlayer);
    }
}
