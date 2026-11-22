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
    public void WhenADeviceAnswersWithHeaders_ThenTheyAreKeptByNameIgnoringCase()
    {
        // Arrange
        using var message = new HttpResponseMessage();
        message.Headers.TryAddWithoutValidation("X-RINCON-HOUSEHOLD", "Sonos_ZwxPaYZW5IrvVUQVXUr4zlXI6L");
        message.Headers.TryAddWithoutValidation("SERVER", "Linux UPnP/1.0 Sonos/85.0-64200 (ZPS40)");
        var device = new DiscoveredSsdpDevice(SearchTarget, message.Headers)
        {
            Usn = "uuid:router",
            DescriptionLocation = new Uri("http://192.168.178.1:49000/tr64desc.xml")
        };

        // Act
        var response = Assert.Single(SsdpSearch.ToResponses([device], SearchTarget));

        // Assert
        Assert.Equal("Sonos_ZwxPaYZW5IrvVUQVXUr4zlXI6L", response.Headers["x-rincon-household"]);
        Assert.Equal("Linux UPnP/1.0 Sonos/85.0-64200 (ZPS40)", response.Headers["Server"]);
    }

    [Fact]
    public void WhenADeviceAnswersWithTooManyOrTooLongHeaders_ThenTheyAreCapped()
    {
        // Arrange
        using var message = new HttpResponseMessage();
        message.Headers.TryAddWithoutValidation("X-LONG-VALUE", new string('v', 1000));
        message.Headers.TryAddWithoutValidation("X-" + new string('n', 300), "dropped");
        for (var index = 0; index < 40; index++)
        {
            message.Headers.TryAddWithoutValidation($"X-HEADER-{index}", "value");
        }

        // Act
        var headers = SsdpSearch.ToHeaders(message.Headers);

        // Assert
        Assert.Equal(32, headers.Count);
        Assert.Equal(new string('v', 256), headers["X-LONG-VALUE"]);
        Assert.DoesNotContain(headers.Keys, name => name.Length > 256);
    }

    [Fact]
    public void WhenAHeaderIsRepeated_ThenItsValuesAreJoined()
    {
        // Arrange
        using var message = new HttpResponseMessage();
        message.Headers.TryAddWithoutValidation("X-REPEATED", "first");
        message.Headers.TryAddWithoutValidation("x-repeated", "second");

        // Act
        var headers = SsdpSearch.ToHeaders(message.Headers);

        // Assert
        Assert.Equal("first, second", Assert.Single(headers).Value);
    }

    private static DiscoveredSsdpDevice Create(string notificationType, string usn, Uri location)
    {
        using var response = new HttpResponseMessage();
        return new DiscoveredSsdpDevice(notificationType, response.Headers) { Usn = usn, DescriptionLocation = location };
    }
}
