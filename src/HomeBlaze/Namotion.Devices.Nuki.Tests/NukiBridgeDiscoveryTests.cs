using System.Net;
using System.Text;
using Namotion.Devices.Nuki.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiBridgeDiscoveryTests
{
    [Fact]
    public async Task WhenDiscoveryServiceListsBridges_ThenBridgesWithAnAddressAreReturned()
    {
        // Arrange
        const string json = """
            {"bridges": [
              {"bridgeId": 2117604523, "ip": "192.168.1.50", "port": 8080, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 42, "ip": "0.0.0.0", "port": 0, "dateUpdated": "2017-06-14T06:53:44Z"}],
             "errorCode": 0}
            """;
        using var httpClient = new HttpClient(new JsonHandler(json));

        // Act
        var bridges = await NukiBridgeDiscovery.DiscoverAsync(httpClient, CancellationToken.None);

        // Assert
        var bridge = Assert.Single(bridges);
        Assert.Equal(2117604523, bridge.BridgeId);
        Assert.Equal("192.168.1.50:8080", bridge.HostAddress);
    }

    [Fact]
    public async Task WhenBridgeConfirmsThePairing_ThenItsTokenIsReturned()
    {
        // Arrange
        var fake = new FakeNukiBridge();

        // Act
        var token = await NukiBridgeDiscovery.PairAsync(fake.CreateClient(string.Empty), "192.168.1.50", CancellationToken.None);

        // Assert
        Assert.Equal("abc123", token);
    }

    [Fact]
    public async Task WhenButtonIsNotPressed_ThenPairingReturnsNull()
    {
        // Arrange
        var fake = new FakeNukiBridge { AuthJson = """{"success": false}""" };

        // Act
        var token = await NukiBridgeDiscovery.PairAsync(fake.CreateClient(string.Empty), "192.168.1.50", CancellationToken.None);

        // Assert
        Assert.Null(token);
    }

    [Fact]
    public async Task WhenBridgeAcceptsTheEncryptedToken_ThenTheTokenModeIsEncrypted()
    {
        // Arrange
        var fake = new FakeNukiBridge();

        // Act
        var mode = await NukiBridgeDiscovery.DetectTokenModeAsync(fake.CreateClient(string.Empty), "192.168.1.50", "abc123", CancellationToken.None);

        // Assert
        Assert.Equal(NukiTokenMode.Encrypted, mode);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task WhenBridgeRejectsTheEncryptedToken_ThenThePlainTokenIsTried()
    {
        // Arrange
        var fake = new FakeNukiBridge();
        fake.StatusOverride = _ => fake.CountRequests("info") == 1 ? HttpStatusCode.Unauthorized : null;

        // Act
        var mode = await NukiBridgeDiscovery.DetectTokenModeAsync(fake.CreateClient(string.Empty), "192.168.1.50", "abc123", CancellationToken.None);

        // Assert
        Assert.Equal(NukiTokenMode.Plain, mode);
        Assert.Contains("token=abc123", fake.Requests[1].Query);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
