using Namotion.Devices.FritzBox.Client;
using Namotion.Devices.FritzBox.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxDiscoveryTests
{
    [Fact]
    public async Task WhenALocationAnswersWithADescription_ThenTheRouterIsDescribed()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var httpClient = new HttpClient();

        // Act
        var result = await FritzBoxDiscovery.DescribeAsync(httpClient, "127.0.0.1", fake.HttpPort, CancellationToken.None);

        // Assert
        Assert.Equal(new FritzBoxDiscoveryResult("127.0.0.1", "FRITZ!Box 7590", "FRITZ!Box 7590", "uuid:75802409-bccb-40e7-8e6c-0123456789ab"), result);
    }

    [Fact]
    public async Task WhenTheRouterDoesNotAnswer_ThenItIsSkipped()
    {
        // Arrange
        using var httpClient = new HttpClient();

        // Act
        var result = await FritzBoxDiscovery.DescribeAsync(httpClient, "127.0.0.1", LoopbackPorts.GetFreePort(), CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task WhenTheDescriptionListsNoDeviceInfo_ThenItIsSkipped()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.DescriptionXml =
            "<root xmlns=\"urn:dslforum-org:device-1-0\"><device><friendlyName>Other</friendlyName><serviceList>" +
            "<service><serviceType>urn:dslforum-org:service:UserInterface:1</serviceType><controlURL>/upnp/control/userif</controlURL></service>" +
            "</serviceList></device></root>";
        using var httpClient = new HttpClient();

        // Act
        var result = await FritzBoxDiscovery.DescribeAsync(httpClient, "127.0.0.1", fake.HttpPort, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task WhenTheDescriptionIsMalformed_ThenItIsSkipped()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.DescriptionXml = "<not-well-formed";
        using var httpClient = new HttpClient();

        // Act
        var result = await FritzBoxDiscovery.DescribeAsync(httpClient, "127.0.0.1", fake.HttpPort, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task WhenTheDescriptionIsOversized_ThenItIsSkipped()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.DescriptionXml = new string('x', Tr64Description.MaximumBytes + 1);
        using var httpClient = new HttpClient { MaxResponseContentBufferSize = Tr64Description.MaximumBytes };

        // Act
        var result = await FritzBoxDiscovery.DescribeAsync(httpClient, "127.0.0.1", fake.HttpPort, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task WhenTheTokenIsAlreadyCancelled_ThenDescribeAsyncThrows()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var httpClient = new HttpClient();
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FritzBoxDiscovery.DescribeAsync(httpClient, "127.0.0.1", fake.HttpPort, cancellationTokenSource.Token));
    }
}
