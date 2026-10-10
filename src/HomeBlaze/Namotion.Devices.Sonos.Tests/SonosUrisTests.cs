using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosUrisTests
{
    [Theory]
    [InlineData(null, SonosSource.None)]
    [InlineData("", SonosSource.None)]
    [InlineData("x-sonos-htastream:RINCON_A0000000000701400:spdif", SonosSource.Tv)]
    [InlineData("x-rincon-stream:RINCON_A0000000000601400", SonosSource.LineIn)]
    [InlineData(TestFixtures.SpotifyConnectUri, SonosSource.SpotifyConnect)]
    [InlineData("x-sonos-vli:RINCON_A0000000000601400:1,airplay:4F9A2B", SonosSource.AirPlay)]
    [InlineData("x-sonos-vli:RINCON_A0000000000601400:3,unknown:1", SonosSource.Other)]
    [InlineData("x-rincon-mp3radio://stream.example.com/live.mp3", SonosSource.Radio)]
    [InlineData("x-sonosapi-stream:tunein%3a9557?sid=303&flags=8232&sn=1", SonosSource.Radio)]
    [InlineData("aac://https://stream.example.com/live", SonosSource.Radio)]
    [InlineData("x-sonosapi-radio:radio%3a1?sid=236", SonosSource.Radio)]
    [InlineData("x-sonosapi-hls:live%3a1?sid=284", SonosSource.Radio)]
    [InlineData("hls-radio://stream.example.com/live.m3u8", SonosSource.Radio)]
    [InlineData("https://stream.example.com/live.mp3", SonosSource.Other)]
    [InlineData("http://files.example.com/chime.mp3", SonosSource.Other)]
    [InlineData("x-rincon-queue:RINCON_A0000000000601400#0", SonosSource.Queue)]
    [InlineData("x-rincon:RINCON_A0000000000101400", SonosSource.Other)]
    public void WhenDetectingSource_ThenMapsUriScheme(string? uri, SonosSource expected)
    {
        // Act
        var source = SonosUris.DetectSource(uri);

        // Assert
        Assert.Equal(expected, source);
    }

    [Fact]
    public void WhenUriIsRelative_ThenItIsResolvedAgainstTheSpeaker()
    {
        // Act
        var uri = SonosUris.ToAbsoluteUri("/getaa?s=1&u=x-sonos-spotify", new Uri("http://10.0.0.121:1400/"));

        // Assert
        Assert.Equal("http://10.0.0.121:1400/getaa?s=1&u=x-sonos-spotify", uri);
    }

    [Fact]
    public void WhenUriIsAbsolute_ThenItIsKept()
    {
        // Act
        var uri = SonosUris.ToAbsoluteUri("https://images.example.com/cover.jpg", new Uri("http://10.0.0.121:1400/"));

        // Assert
        Assert.Equal("https://images.example.com/cover.jpg", uri);
    }

    [Fact]
    public void WhenConvertingHttpStream_ThenUsesRadioScheme()
    {
        // Act
        var uri = SonosUris.ToStreamUri("http://stream.example.com/live.mp3");

        // Assert
        Assert.Equal("x-rincon-mp3radio://stream.example.com/live.mp3", uri);
    }
}
