using System.Text;
using System.Xml;
using Namotion.Devices.FritzBox.Parsing;
using Namotion.Devices.FritzBox.Tests.Testing;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class HostListParserTests
{
    [Fact]
    public void WhenTheHostListIsParsed_ThenEveryItemIsReadWithItsValues()
    {
        // Arrange
        using var stream = Fixtures.Open("hostlist.xml");

        // Act
        var hosts = HostListParser.Parse(stream);

        // Assert
        Assert.Equal(5, hosts.Count);
        Assert.Equal(new HostEntry("AA:AA:AA:00:00:01", "192.168.178.20", true, "laptop", "802.11", 0, 144, false, true, false, "filtprof1"), hosts[0]);
        Assert.Equal(new HostEntry("aa:aa:aa:00:00:02", "192.168.178.21", true, "desktop", "Ethernet", 1, 1000, false, false, true, "filtprof1"), hosts[1]);
        Assert.False(hosts[2].IsActive);
        Assert.True(hosts[2].IsGuest);
        Assert.Equal(string.Empty, hosts[4].MacAddress);
    }

    [Fact]
    public void WhenAnItemIsEmpty_ThenItIsReadWithoutValues()
    {
        // Arrange
        using var stream = ToStream("<List><Item /><Item><HostName /></Item></List>");

        // Act
        var hosts = HostListParser.Parse(stream);

        // Assert
        Assert.Equal(2, hosts.Count);
        Assert.Equal(new HostEntry(null, null, false, null, null, null, null, false), hosts[0]);
        Assert.Equal(string.Empty, hosts[1].HostName);
    }

    [Theory]
    [InlineData("<!DOCTYPE List [<!ENTITY e \"x\">]><List><Item><HostName>&e;</HostName></Item></List>")]
    [InlineData("<List><Item><HostName>laptop</HostName>")]
    [InlineData("<List><Item><HostName><b>nested</b></HostName></Item></List>")]
    public void WhenTheHostListIsMalformed_ThenParsingThrowsXmlException(string xml)
    {
        // Arrange
        using var stream = ToStream(xml);

        // Act & Assert
        Assert.Throws<XmlException>(() => HostListParser.Parse(stream));
    }

    [Theory]
    [InlineData("granted", true)]
    [InlineData("denied", false)]
    [InlineData(" granted ", true)]
    [InlineData("error", null)]
    [InlineData("unknown", null)]
    [InlineData("", null)]
    public void WhenTheWanAccessIsRead_ThenOnlyGrantedAndDeniedAreKnown(string value, bool? expected)
    {
        // Arrange
        using var stream = ToStream($"<List><Item><X_AVM-DE_WANAccess>{value}</X_AVM-DE_WANAccess></Item></List>");

        // Act
        var hosts = HostListParser.Parse(stream);

        // Assert
        Assert.Equal(expected, hosts[0].HasWanAccess);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("", null)]
    public void WhenTheDisallowFlagIsRead_ThenItIsABooleanOrNull(string value, bool? expected)
    {
        // Arrange
        using var stream = ToStream($"<List><Item><X_AVM-DE_Disallow>{value}</X_AVM-DE_Disallow></Item></List>");

        // Act
        var hosts = HostListParser.Parse(stream);

        // Assert
        Assert.Equal(expected, hosts[0].IsWanAccessDisallowed);
    }

    [Fact]
    public void WhenTheFirmwareOmitsTheInternetAccessFields_ThenTheyAreNull()
    {
        // Arrange
        using var stream = ToStream("<List><Item><HostName>laptop</HostName></Item></List>");

        // Act
        var hosts = HostListParser.Parse(stream);

        // Assert
        Assert.Null(hosts[0].HasWanAccess);
        Assert.Null(hosts[0].IsWanAccessDisallowed);
        Assert.Null(hosts[0].FilterProfileId);
    }

    [Fact]
    public void WhenThePortIsNotNumeric_ThenItIsNull()
    {
        // Arrange
        using var stream = ToStream("<List><Item><X_AVM-DE_Port>not-a-number</X_AVM-DE_Port></Item></List>");

        // Act
        var hosts = HostListParser.Parse(stream);

        // Assert
        Assert.Null(hosts[0].Port);
    }

    [Fact]
    public void WhenTheHostListNestsTooDeeply_ThenParsingThrowsXmlException()
    {
        // Arrange
        using var stream = ToStream("<List>" + string.Concat(Enumerable.Repeat("<a>", 1000)) + string.Concat(Enumerable.Repeat("</a>", 1000)) + "</List>");

        // Act & Assert
        Assert.Throws<XmlException>(() => HostListParser.Parse(stream));
    }

    private static MemoryStream ToStream(string xml) => new(Encoding.UTF8.GetBytes(xml));
}
