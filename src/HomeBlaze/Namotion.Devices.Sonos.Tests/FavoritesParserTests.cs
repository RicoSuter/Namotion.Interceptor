using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class FavoritesParserTests
{
    [Fact]
    public void WhenParsingFavorites_ThenShortcutsWithoutUriAreSkipped()
    {
        // Act
        var favorites = FavoritesParser.Parse(TestFixtures.Read("favorites.xml"));

        // Assert
        Assert.Equal(new[] { "Radio FM1", "SRF 3" }, favorites.Select(favorite => favorite.Title));
    }

    [Fact]
    public void WhenParsingStreamFavorite_ThenUriAndStoredMetadataAreKept()
    {
        // Act
        var radio = FavoritesParser.Parse(TestFixtures.Read("favorites.xml")).Single(favorite => favorite.Title == "Radio FM1");

        // Assert
        Assert.Equal("x-sonosapi-stream:tunein%3a9557?sid=303&flags=8232&sn=1", radio.Uri);
        Assert.Contains("<dc:title>Radio FM1</dc:title>", radio.Metadata);
        Assert.False(radio.IsContainer);
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
        var favorite = Assert.Single(FavoritesParser.Parse(result));

        // Assert
        Assert.True(favorite.IsContainer);
    }
}
