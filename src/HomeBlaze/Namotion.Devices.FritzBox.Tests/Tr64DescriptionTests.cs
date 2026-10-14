using System.Xml;
using Namotion.Devices.FritzBox.Client;
using Namotion.Devices.FritzBox.Tests.Testing;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class Tr64DescriptionTests
{
    [Fact]
    public void WhenTheDescriptionIsParsed_ThenItsIdentityAndEveryNestedServiceAreRead()
    {
        // Act
        var description = Tr64Description.Parse(Fixtures.Read("tr64desc.xml"));

        // Assert
        Assert.Equal("FRITZ!Box 7590", description.FriendlyName);
        Assert.Equal("FRITZ!Box 7590", description.ModelName);
        Assert.Equal("uuid:75802409-bccb-40e7-8e6c-0123456789ab", description.Udn);
        Assert.Equal(10, description.ControlPaths.Count);
        Assert.Equal("/upnp/control/hosts", description.ControlPaths[Tr64Services.Hosts]);
        Assert.Equal("/upnp/control/wanpppconn1", description.ControlPaths[Tr64Services.WanPppConnection]);
        Assert.Equal(
            ["urn:dslforum-org:service:WLANConfiguration:1", "urn:dslforum-org:service:WLANConfiguration:2", "urn:dslforum-org:service:WLANConfiguration:3"],
            description.WlanServiceTypes);
    }

    [Fact]
    public void WhenAControlUrlIsNotAPathOnTheSameHost_ThenItsServiceIsLeftOut()
    {
        // Arrange
        const string xml =
            "<root xmlns=\"urn:dslforum-org:device-1-0\"><device><serviceList>" +
            "<service><serviceType>urn:dslforum-org:service:Hosts:1</serviceType><controlURL>http://attacker/upnp/control/hosts</controlURL></service>" +
            "<service><serviceType>urn:dslforum-org:service:DeviceInfo:1</serviceType><controlURL>//attacker/upnp/control/deviceinfo</controlURL></service>" +
            "<service><serviceType>urn:dslforum-org:service:UserInterface:1</serviceType><controlURL>/upnp/control/userif</controlURL></service>" +
            "</serviceList></device></root>";

        // Act
        var description = Tr64Description.Parse(xml);

        // Assert
        Assert.Equal([Tr64Services.UserInterface], description.ControlPaths.Keys);
    }

    [Theory]
    [InlineData("/upnp/control/hosts", true)]
    [InlineData("/devicehostlist.lua?sid=1", true)]
    [InlineData("/", true)]
    [InlineData("//attacker/x", false)]
    [InlineData("/\\attacker/x", false)]
    [InlineData("http://attacker/x", false)]
    [InlineData("/redirect?to=http://attacker", false)]
    [InlineData("upnp/control/hosts", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void WhenAPathIsChecked_ThenOnlyARootedPathOnTheSameHostIsLocal(string? value, bool expected)
    {
        // Act
        var isLocal = Tr64Description.IsLocalPath(value);

        // Assert
        Assert.Equal(expected, isLocal);
    }

    [Fact]
    public void WhenTheDescriptionDeclaresADocumentType_ThenParsingThrowsXmlException()
    {
        // Act & Assert
        Assert.Throws<XmlException>(() => Tr64Description.Parse("<!DOCTYPE root [<!ENTITY e \"x\">]><root>&e;</root>"));
    }
}
