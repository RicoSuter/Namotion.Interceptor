using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class DeviceDescriptionParserTests
{
    [Fact]
    public void WhenParsingRayDescription_ThenModelAndServicesAreRead()
    {
        // Act
        var description = DeviceDescriptionParser.Parse(TestFixtures.Read("device-description-ray.xml"));

        // Assert
        Assert.Equal("Sonos Ray", description.ModelName);
        Assert.Equal("S36", description.ModelNumber);
        Assert.Contains("HTControl", description.ServiceIds);
        Assert.Contains("AVTransport", description.ServiceIds);
        Assert.Contains("GroupRenderingControl", description.ServiceIds);
        Assert.DoesNotContain("AudioIn", description.ServiceIds);
    }
}
