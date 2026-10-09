using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class UpnpEventParserTests
{
    private const string SpotifyUri = "x-sonos-vli:RINCON_A0000000000601400:2,spotify:94963e711df088cf";

    [Fact]
    public void WhenAvTransportEventHasSpotifyTrack_ThenAllFieldsAreRead()
    {
        // Arrange
        var body = SonosEventBodies.AvTransport(
            ("TransportState", "PLAYING"),
            ("CurrentPlayMode", "SHUFFLE_NOREPEAT"),
            ("CurrentTrackURI", SpotifyUri),
            ("CurrentTrackDuration", "0:03:25"),
            ("CurrentTrackMetaData", SonosEventBodies.Didl("Song", "Artist", "Album", "/getaa?s=1&u=x")),
            ("AVTransportURI", SpotifyUri));

        // Act
        var change = UpnpEventParser.ParseAvTransport(body);

        // Assert
        Assert.Equal("PLAYING", change.TransportState);
        Assert.Equal("SHUFFLE_NOREPEAT", change.PlayMode);
        Assert.Equal(SpotifyUri, change.TrackUri);
        Assert.Equal(SpotifyUri, change.MediaUri);
        Assert.Equal("0:03:25", change.TrackDuration);
        Assert.Contains("<dc:title>Song</dc:title>", change.TrackMetaData);
    }

    [Fact]
    public void WhenAvTransportEventOmitsFields_ThenTheyAreNull()
    {
        // Arrange
        var body = SonosEventBodies.AvTransport(("TransportState", "STOPPED"));

        // Act
        var change = UpnpEventParser.ParseAvTransport(body);

        // Assert
        Assert.Equal("STOPPED", change.TransportState);
        Assert.Null(change.PlayMode);
        Assert.Null(change.TrackUri);
        Assert.Null(change.TrackMetaData);
    }

    [Fact]
    public void WhenRenderingControlEvent_ThenOnlyMasterChannelValuesAreRead()
    {
        // Arrange
        var body = SonosEventBodies.RenderingControl(
            ("Volume", "LF", "100"),
            ("Volume", "Master", "22"),
            ("Mute", "Master", "1"),
            ("Bass", null, "-2"),
            ("Treble", null, "3"),
            ("Loudness", "Master", "1"),
            ("NightMode", null, "1"),
            ("DialogLevel", null, "0"));

        // Act
        var change = UpnpEventParser.ParseRenderingControl(body);

        // Assert
        Assert.Equal(new RenderingControlChange(22, true, -2, 3, true, true, false), change);
    }

    [Fact]
    public void WhenGroupRenderingControlEvent_ThenGroupVolumeAndMuteAreRead()
    {
        // Arrange
        var body = SonosEventBodies.Properties(("GroupMute", "0"), ("GroupVolume", "35"), ("GroupVolumeChangeable", "1"));

        // Act
        var change = UpnpEventParser.ParseGroupRenderingControl(body);

        // Assert
        Assert.Equal(new GroupRenderingControlChange(35, false), change);
    }

    [Fact]
    public void WhenTopologyEvent_ThenZoneGroupStateIsReturned()
    {
        // Arrange
        var zoneGroupState = TestFixtures.Read("zone-group-state.xml");
        var body = SonosEventBodies.Properties(("ZoneGroupState", zoneGroupState), ("ThirdPartyMediaServersX", "x"));

        // Act
        var result = UpnpEventParser.ParseZoneGroupState(body);

        // Assert
        Assert.Equal(zoneGroupState, result);
    }
}
