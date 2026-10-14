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
        Assert.All(
            [Tr64Services.DeviceInfo, Tr64Services.UserInterface, Tr64Services.Layer3Forwarding, Tr64Services.WanCommonInterfaceConfig,
             Tr64Services.WanPppConnection, Tr64Services.WanIpConnection, Tr64Services.Hosts],
            serviceType => Assert.True(description.HasService(serviceType), serviceType));
        Assert.Equal("/upnp/control/hosts", description.TryGetService(Tr64Services.Hosts)!.ControlPath);
        Assert.Equal("/upnp/control/wanpppconn1", description.TryGetService(Tr64Services.WanPppConnection)!.ControlPath);
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
        Assert.True(description.HasService(Tr64Services.UserInterface));
        Assert.False(description.HasService(Tr64Services.Hosts));
        Assert.False(description.HasService(Tr64Services.DeviceInfo));
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

    [Fact]
    public void WhenThereAreMoreThanNineWlans_ThenTheyStillSortNumerically()
    {
        // Arrange
        const string xml =
            "<root xmlns=\"urn:dslforum-org:device-1-0\"><device><serviceList>" +
            "<service><serviceType>urn:dslforum-org:service:WLANConfiguration:10</serviceType><controlURL>/upnp/control/wlanconfig10</controlURL></service>" +
            "<service><serviceType>urn:dslforum-org:service:WLANConfiguration:2</serviceType><controlURL>/upnp/control/wlanconfig2</controlURL></service>" +
            "</serviceList></device></root>";

        // Act
        var description = Tr64Description.Parse(xml);

        // Assert
        Assert.Equal(
            ["urn:dslforum-org:service:WLANConfiguration:2", "urn:dslforum-org:service:WLANConfiguration:10"],
            description.WlanServiceTypes);
    }

    [Fact]
    public void WhenAServiceTypeAppearsTwice_ThenTheFirstControlPathIsKept()
    {
        // Arrange
        const string xml =
            "<root xmlns=\"urn:dslforum-org:device-1-0\"><device><serviceList>" +
            "<service><serviceType>urn:dslforum-org:service:Hosts:1</serviceType><controlURL>/upnp/control/hosts-first</controlURL></service>" +
            "<service><serviceType>urn:dslforum-org:service:Hosts:1</serviceType><controlURL>/upnp/control/hosts-second</controlURL></service>" +
            "</serviceList></device></root>";

        // Act
        var description = Tr64Description.Parse(xml);

        // Assert
        Assert.Equal("/upnp/control/hosts-first", description.TryGetService(Tr64Services.Hosts)!.ControlPath);
    }
}
