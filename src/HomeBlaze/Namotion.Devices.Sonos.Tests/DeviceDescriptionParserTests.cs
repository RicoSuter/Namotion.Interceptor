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

    [Fact]
    public void WhenAServiceIdHoldsElements_ThenOnlyTheServiceIdsWithoutElementsAreRead()
    {
        // Arrange
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <serviceList>
                  <service><serviceId>urn:upnp-org:serviceId:AVTransport</serviceId></service>
                  <service><serviceId>urn:upnp-org:serviceId:Before<serviceId>urn:upnp-org:serviceId:Nested</serviceId>:After</serviceId></service>
                </serviceList>
              </device>
            </root>
            """;

        // Act
        var description = DeviceDescriptionParser.Parse(xml);

        // Assert
        Assert.Equal(["AVTransport", "Nested"], description.ServiceIds.Order());
    }

    [Fact]
    public void WhenServiceIdsNestAroundALongText_ThenTheTextIsNotCopiedForEveryLevel()
    {
        // Arrange: as deep as a document may nest. Allocations are counted instead of time, which a slow machine
        // stretches: reading the text takes 2 MB, and copying it for every level 130 MB.
        const int levels = SonosXml.MaximumLevels - 1;
        const long maximumAllocatedBytes = 16 * 1024 * 1024;
        var text = new string('x', 256 * 1024);
        var xml =
            "<root xmlns=\"urn:schemas-upnp-org:device-1-0\">" +
            string.Concat(Enumerable.Repeat("<serviceId>", levels)) + text + string.Concat(Enumerable.Repeat("</serviceId>", levels)) +
            "</root>";

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var description = DeviceDescriptionParser.Parse(xml);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        Assert.Equal([text], description.ServiceIds);
        Assert.InRange(allocatedBytes, 0, maximumAllocatedBytes);
    }
}
