using System.Xml;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosXmlTests
{
    private const int OneLevelTooMany = SonosXml.MaximumLevels + 1;

    // The parsers take a document from a speaker on the network, so each one is an entry point for a hostile one.
    public static TheoryData<string, Action<string>> Parsers => new()
    {
        { "AVTransport event", document => UpnpEventParser.ParseAvTransport(document) },
        { "RenderingControl event", document => UpnpEventParser.ParseRenderingControl(document) },
        { "GroupRenderingControl event", document => UpnpEventParser.ParseGroupRenderingControl(document) },
        { "ZoneGroupTopology event", document => UpnpEventParser.ParseZoneGroupState(document) },
        { "LastChange of an AVTransport event", document => UpnpEventParser.ParseAvTransport(SonosEventBodies.Properties(("LastChange", document))) },
        { "LastChange of a RenderingControl event", document => UpnpEventParser.ParseRenderingControl(SonosEventBodies.Properties(("LastChange", document))) },
        { "zone group state", document => ZoneGroupStateParser.Parse(document) },
        { "favorites", document => FavoritesParser.Parse(document, new Uri("http://127.0.0.1:1400/")) },
        { "device description", document => DeviceDescriptionParser.Parse(document) }
    };

    [Fact]
    public void WhenADocumentNestsAsManyLevelsAsAllowed_ThenItParsesWithTheTextOfItsInnermostElement()
    {
        // Act
        var document = SonosXml.Parse(Nest(SonosXml.MaximumLevels, "text"));

        // Assert
        Assert.Equal(SonosXml.MaximumLevels, document.Descendants().Count());
        Assert.Equal("text", document.Root!.Value);
    }

    [Fact]
    public void WhenADocumentNestsOneLevelMoreThanAllowed_ThenParsingThrowsXmlException()
    {
        // Act
        var exception = Assert.Throws<XmlException>(() => SonosXml.Parse(Nest(OneLevelTooMany)));

        // Assert
        Assert.Contains("deeper", exception.Message);
    }

    [Fact]
    public void WhenADocumentNestsFarDeeperThanAllowed_ThenParsingStopsAtTheLimitInsteadOfReadingOn()
    {
        // Arrange: never closed, so a parse that read to the end would report the end of the file instead.
        var unclosed = string.Concat(Enumerable.Repeat("<a>", 50_000));

        // Act
        var exception = Assert.Throws<XmlException>(() => SonosXml.Parse(unclosed));

        // Assert
        Assert.Contains("deeper", exception.Message);
    }

    [Fact]
    public void WhenADocumentIsWideButNotDeep_ThenItParsesWhateverItsSize()
    {
        // Arrange
        var wide = "<a>" + string.Concat(Enumerable.Repeat("<b><c>1</c></b>", 20_000)) + "</a>";

        // Act
        var document = SonosXml.Parse(wide);

        // Assert
        Assert.Equal(20_000, document.Root!.Elements().Count());
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public void WhenADocumentForAParserNestsOneLevelTooMany_ThenTheParserThrowsXmlException(string name, Action<string> parse)
    {
        // Arrange
        var document = Nest(OneLevelTooMany);

        // Act & Assert
        Assert.False(string.IsNullOrEmpty(name));
        Assert.Throws<XmlException>(() => parse(document));
    }

    [Fact]
    public void WhenTrackMetadataNestsTooDeeply_ThenItIsReadAsNoMetadata()
    {
        // Arrange: a track with a title, so only the nesting inside its item makes it unreadable.
        var readable = SonosEventBodies.Didl("Song");
        var tooDeep = readable.Replace("</item>", Nest(SonosXml.MaximumLevels) + "</item>", StringComparison.Ordinal);

        // Act
        var readableTitle = DidlParser.ParseTitle(readable);
        var track = DidlParser.ParseTrack(tooDeep);
        var title = DidlParser.ParseTitle(tooDeep);

        // Assert
        Assert.Equal("Song", readableTitle);
        Assert.NotEqual(readable, tooDeep);
        Assert.Null(track);
        Assert.Null(title);
    }

    [Fact]
    public void WhenTheFixtureDocumentsOfASpeakerAreParsed_ThenTheyStayFarBelowTheLimit()
    {
        // Act
        var deepest = new[] { "zone-group-state.xml", "favorites.xml", "device-description-ray.xml" }
            .Max(name => SonosXml.Parse(TestFixtures.Read(name)).Descendants().Max(element => element.Ancestors().Count() + 1));

        // Assert
        Assert.InRange(deepest, 1, SonosXml.MaximumLevels / 4);
    }

    private static string Nest(int levels, string text = "") =>
        string.Concat(Enumerable.Repeat("<a>", levels)) + text + string.Concat(Enumerable.Repeat("</a>", levels));
}
