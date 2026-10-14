using Namotion.Devices.FritzBox.Tests.Testing;
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
        var result = await FritzBoxDiscovery.DescribeAsync(httpClient, new Uri($"http://127.0.0.1:{fake.HttpPort}/tr64desc.xml"), CancellationToken.None);

        // Assert
        Assert.Equal(new FritzBoxDiscoveryResult("127.0.0.1", "FRITZ!Box 7590", "FRITZ!Box 7590", "uuid:75802409-bccb-40e7-8e6c-0123456789ab"), result);
    }

    [Fact]
    public async Task WhenALocationDoesNotAnswer_ThenItIsSkipped()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var httpClient = new HttpClient();

        // Act
        var result = await FritzBoxDiscovery.DescribeAsync(httpClient, new Uri($"http://127.0.0.1:{fake.HttpPort}/missing.xml"), CancellationToken.None);

        // Assert
        Assert.Null(result);
    }
}
