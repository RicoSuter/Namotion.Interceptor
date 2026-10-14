using System.Text;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace Namotion.Devices.Upnp.Tests;

public class UpnpXmlTests
{
    private const int OneLevelTooMany = UpnpXml.MaximumLevels + 1;

    [Fact]
    public void WhenADocumentNestsAsManyLevelsAsAllowed_ThenItParsesWithTheTextOfItsInnermostElement()
    {
        // Act
        var document = UpnpXml.Parse(Nest(UpnpXml.MaximumLevels, "text"));

        // Assert
        Assert.Equal(UpnpXml.MaximumLevels, document.Descendants().Count());
        Assert.Equal("text", document.Root!.Value);
    }

    [Fact]
    public void WhenADocumentNestsOneLevelMoreThanAllowed_ThenParsingThrowsXmlException()
    {
        // Act
        var exception = Assert.Throws<XmlException>(() => UpnpXml.Parse(Nest(OneLevelTooMany)));

        // Assert
        Assert.Contains("deeper", exception.Message);
    }

    [Fact]
    public void WhenAStreamNestsOneLevelMoreThanAllowed_ThenReadingItThrowsXmlException()
    {
        // Arrange
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Nest(OneLevelTooMany)));
        using var reader = UpnpXml.CreateReader(stream);

        // Act
        var exception = Assert.Throws<XmlException>(() => XDocument.Load(reader));

        // Assert
        Assert.Contains("deeper", exception.Message);
    }

    [Fact]
    public void WhenADocumentNestsFarDeeperThanAllowed_ThenParsingStopsAtTheLimitInsteadOfReadingOn()
    {
        // Arrange: never closed, so a parse that read to the end would report the end of the file instead.
        var unclosed = string.Concat(Enumerable.Repeat("<a>", 50_000));

        // Act
        var exception = Assert.Throws<XmlException>(() => UpnpXml.Parse(unclosed));

        // Assert
        Assert.Contains("deeper", exception.Message);
    }

    [Fact]
    public void WhenADocumentIsWideButNotDeep_ThenItParsesWhateverItsSize()
    {
        // Arrange
        var wide = "<a>" + string.Concat(Enumerable.Repeat("<b><c>1</c></b>", 20_000)) + "</a>";

        // Act
        var document = UpnpXml.Parse(wide);

        // Assert
        Assert.Equal(20_000, document.Root!.Elements().Count());
    }

    [Fact]
    public void WhenADocumentDeclaresADocumentType_ThenParsingThrowsXmlException()
    {
        // Act & Assert
        Assert.Throws<XmlException>(() => UpnpXml.Parse("<!DOCTYPE a [<!ENTITY e \"x\">]><a>&e;</a>"));
    }

    private static string Nest(int levels, string text = "") =>
        string.Concat(Enumerable.Repeat("<a>", levels)) + text + string.Concat(Enumerable.Repeat("</a>", levels));
}
