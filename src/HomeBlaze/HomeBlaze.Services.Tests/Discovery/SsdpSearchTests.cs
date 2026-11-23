using HomeBlaze.Services.Discovery;
using Rssdp;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class SsdpSearchTests
{
    private const string SearchTarget = "urn:dslforum-org:device:InternetGatewayDevice:1";

    [Fact]
    public void WhenDevicesAnswer_ThenOnlyAnswersForTheTargetWithAnAbsoluteLocationAreKept()
    {
        // Arrange
        DiscoveredSsdpDevice[] devices =
        [
            Create(SearchTarget, "uuid:router", new Uri("http://192.168.178.1:49000/tr64desc.xml")),
            Create("upnp:rootdevice", "uuid:other", new Uri("http://192.168.178.2/desc.xml")),
            Create(SearchTarget, "uuid:relative", new Uri("desc.xml", UriKind.Relative))
        ];

        // Act
        var responses = SsdpSearch.ToResponses(devices, SearchTarget);

        // Assert
        var response = Assert.Single(responses);
        Assert.Equal("192.168.178.1", response.Location.Host);
        Assert.Equal("uuid:router", response.UniqueServiceName);
    }

    [Fact]
    public void WhenMoreAnswersMatchThanTheLimit_ThenOnly64AreReturned()
    {
        // Arrange
        var devices = Enumerable.Range(0, 65)
            .Select(index => Create(SearchTarget, $"uuid:router-{index}", new Uri($"http://192.168.178.{index + 1}:49000/tr64desc.xml")))
            .ToArray();

        // Act
        var responses = SsdpSearch.ToResponses(devices, SearchTarget);

        // Assert
        Assert.Equal(64, responses.Count);
        Assert.Equal("uuid:router-63", responses[^1].UniqueServiceName);
    }

    private static DiscoveredSsdpDevice Create(string notificationType, string usn, Uri location)
    {
        using var response = new HttpResponseMessage();
        return new DiscoveredSsdpDevice(notificationType, response.Headers) { Usn = usn, DescriptionLocation = location };
    }
}
