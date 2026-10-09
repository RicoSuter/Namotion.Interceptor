using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class DidlParserTests
{
    [Fact]
    public void WhenMetadataHasTrack_ThenTitleArtistAlbumAndArtAreRead()
    {
        // Arrange
        var metadata = SonosEventBodies.Didl("Song", "Artist", "Album", "/getaa?s=1&u=x");

        // Act
        var track = DidlParser.ParseTrack(metadata);

        // Assert
        Assert.Equal(new DidlTrack("Song", "Artist", "Album", "/getaa?s=1&u=x"), track);
    }

    [Fact]
    public void WhenMetadataHasStreamContent_ThenStreamContentIsTheTitle()
    {
        // Arrange
        var metadata = SonosEventBodies.Didl("x-sonosapi-stream:tunein", streamContent: "Artist - Live Song");

        // Act
        var track = DidlParser.ParseTrack(metadata);

        // Assert
        Assert.Equal("Artist - Live Song", track?.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NOT_IMPLEMENTED")]
    [InlineData("<not-xml")]
    [InlineData("<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" />")]
    public void WhenMetadataIsMissingOrInvalid_ThenReturnsNull(string? metadata)
    {
        // Act
        var track = DidlParser.ParseTrack(metadata);

        // Assert
        Assert.Null(track);
    }
}
