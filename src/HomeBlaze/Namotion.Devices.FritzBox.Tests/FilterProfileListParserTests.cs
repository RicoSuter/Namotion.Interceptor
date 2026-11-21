using System.Xml;
using Namotion.Devices.FritzBox.Parsing;
using Namotion.Devices.FritzBox.Tests.Testing;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FilterProfileListParserTests
{
    [Fact]
    public void WhenTheFilterProfileListIsParsed_ThenEveryNameIsReadByItsId()
    {
        // Arrange
        var xml = Fixtures.Read("filterprofiles.xml");

        // Act
        var profiles = FilterProfileListParser.Parse(xml);

        // Assert
        Assert.Equal(2, profiles.Count);
        Assert.Equal("Standard", profiles["filtprof1"]);
        Assert.Equal("Gast", profiles["filtprof2"]);
    }

    [Fact]
    public void WhenTheProfilesHaveNoCommonRoot_ThenEveryProfileIsRead()
    {
        // Arrange
        const string xml =
            "<FilterProfile><FilterProfileID>filtprof1</FilterProfileID><Name>Standard</Name></FilterProfile>" +
            "<FilterProfile><Name>Kinder</Name><FilterProfileID>filtprof3</FilterProfileID></FilterProfile>";

        // Act
        var profiles = FilterProfileListParser.Parse(xml);

        // Assert
        Assert.Equal("Standard", profiles["filtprof1"]);
        Assert.Equal("Kinder", profiles["filtprof3"]);
    }

    [Fact]
    public void WhenAProfileHasNoIdOrNoName_ThenItIsSkipped()
    {
        // Arrange
        const string xml =
            "<List><FilterProfile><Name>Without ID</Name></FilterProfile><FilterProfile><FilterProfileID>filtprof2</FilterProfileID><Name> </Name></FilterProfile>" +
            "<FilterProfile /><FilterProfile><FilterProfileID> filtprof1 </FilterProfileID><Name> Standard </Name></FilterProfile></List>";

        // Act
        var profiles = FilterProfileListParser.Parse(xml);

        // Assert
        Assert.Equal(new Dictionary<string, string> { ["filtprof1"] = "Standard" }, profiles);
    }

    [Fact]
    public void WhenATimeTableHoldsANameElement_ThenItDoesNotReplaceTheProfileName()
    {
        // Arrange
        const string xml = "<FilterProfile><FilterProfileID>filtprof1</FilterProfileID><Name>Standard</Name><TimeTable><Name>Other</Name></TimeTable></FilterProfile>";

        // Act
        var profiles = FilterProfileListParser.Parse(xml);

        // Assert
        Assert.Equal("Standard", profiles["filtprof1"]);
    }

    [Theory]
    [InlineData("<!DOCTYPE List [<!ENTITY e \"x\">]><List><FilterProfile><Name>&e;</Name></FilterProfile></List>")]
    [InlineData("<List><FilterProfile><Name>Standard</Name>")]
    public void WhenTheFilterProfileListIsMalformed_ThenParsingThrowsXmlException(string xml)
    {
        // Act & Assert
        Assert.Throws<XmlException>(() => FilterProfileListParser.Parse(xml));
    }
}
