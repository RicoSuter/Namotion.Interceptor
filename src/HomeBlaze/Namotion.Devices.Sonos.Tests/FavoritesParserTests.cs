using System.Text.Json;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class FavoritesParserTests
{
    private static readonly Uri SpeakerUri = new("http://192.168.1.20:1400/");

    [Fact]
    public void WhenParsingFavorites_ThenShortcutsWithoutUriAreSkipped()
    {
        // Act
        var favorites = FavoritesParser.Parse(TestFixtures.Read("favorites.xml"), SpeakerUri);

        // Assert
        Assert.Equal(new[] { "Radio FM1", "SRF 3" }, favorites.Select(favorite => favorite.Title));
    }

    [Fact]
    public void WhenParsingStreamFavorite_ThenUriAndStoredMetadataAreKept()
    {
        // Act
        var radio = FavoritesParser.Parse(TestFixtures.Read("favorites.xml"), SpeakerUri).Single(favorite => favorite.Title == "Radio FM1");

        // Assert
        Assert.Equal("x-sonosapi-stream:tunein%3a9557?sid=303&flags=8232&sn=1", radio.Uri);
        Assert.Contains("<dc:title>Radio FM1</dc:title>", radio.Metadata);
        Assert.False(radio.IsContainer);
    }

    [Fact]
    public void WhenFavoriteHasAbsoluteAlbumArt_ThenImageUriIsKept()
    {
        // Act
        var radio = FavoritesParser.Parse(TestFixtures.Read("favorites.xml"), SpeakerUri).Single(favorite => favorite.Title == "Radio FM1");

        // Assert
        Assert.Equal("https://sali.sonos.radio/image?w=60&image=https%3A%2F%2Fcdn-profiles.tunein.com%2Fs25077%2Fimages%2Flogog.jpg&partnerId=tunein", radio.ImageUri);
    }

    [Fact]
    public void WhenFavoriteHasRelativeAlbumArt_ThenImageUriIsResolvedAgainstTheSpeaker()
    {
        // Act
        var station = FavoritesParser.Parse(TestFixtures.Read("favorites.xml"), SpeakerUri).Single(favorite => favorite.Title == "SRF 3");

        // Assert
        Assert.Equal("http://192.168.1.20:1400/getaa?s=1&u=x-sonosapi-stream%3atunein%3a9464", station.ImageUri);
    }

    [Fact]
    public void WhenFavoriteHasNoAlbumArt_ThenImageUriIsNull()
    {
        // Arrange
        const string result = """
            <DIDL-Lite xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/">
              <item id="FV:2/9" parentID="FV:2"><dc:title>Road Trip</dc:title><res>x-sonosapi-stream:tunein%3a1</res></item>
            </DIDL-Lite>
            """;

        // Act
        var favorite = Assert.Single(FavoritesParser.Parse(result, SpeakerUri));

        // Assert
        Assert.Null(favorite.ImageUri);
    }

    [Fact]
    public void WhenSerializingFavorite_ThenStoredMetadataIsNotIncluded()
    {
        // Arrange
        var favorite = FavoritesParser.Parse(TestFixtures.Read("favorites.xml"), SpeakerUri).Single(favorite => favorite.Title == "Radio FM1");

        // Act
        var json = JsonSerializer.Serialize(favorite);

        // Assert
        Assert.Contains("Radio FM1", json);
        Assert.DoesNotContain("Metadata", json);
        Assert.DoesNotContain("SA_RINCON", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n ")]
    public void WhenResultIsBlank_ThenNoFavoritesAreReturned(string? result)
    {
        // Act
        var favorites = FavoritesParser.Parse(result, SpeakerUri);

        // Assert
        Assert.Empty(favorites);
    }

    [Fact]
    public void WhenFavoriteIsContainer_ThenIsContainerIsTrue()
    {
        // Arrange
        const string result = """
            <DIDL-Lite xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:r="urn:schemas-rinconnetworks-com:metadata-1-0/" xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/">
              <item id="FV:2/9" parentID="FV:2"><dc:title>Road Trip</dc:title><res>x-rincon-cpcontainer:1006206cspotify%3aplaylist%3a1?sid=9&amp;flags=8300&amp;sn=1</res><r:resMD>&lt;DIDL-Lite/&gt;</r:resMD></item>
            </DIDL-Lite>
            """;

        // Act
        var favorite = Assert.Single(FavoritesParser.Parse(result, SpeakerUri));

        // Assert
        Assert.True(favorite.IsContainer);
    }
}
