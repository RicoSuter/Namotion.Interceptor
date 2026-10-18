using System.Net;
using System.Text;
using Namotion.Devices.Nuki.Client;
using Namotion.Devices.Nuki.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiBridgeDiscoveryTests
{
    [Fact]
    public async Task WhenDiscoveryServiceListsBridges_ThenOnlyBridgesWithAnIpAddressAndAValidPortAreReturned()
    {
        // Arrange
        const string json = """
            {"bridges": [
              {"bridgeId": 2117604523, "ip": "192.168.1.50", "port": 8080, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 7, "ip": "fe80::1", "port": 8081, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 42, "ip": "0.0.0.0", "port": 0, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 43, "ip": "192.168.1.51/path", "port": 8080, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 44, "ip": "nuki.local", "port": 8080, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 45, "ip": "192.168.1.52", "port": 65536, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 46, "ip": "192.168.1.53", "port": -1, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 47, "port": 8080, "dateUpdated": "2017-06-14T06:53:44Z"}],
             "errorCode": 0}
            """;
        var handler = new JsonHandler(json);
        using var httpClient = new HttpClient(handler);

        // Act
        var bridges = await NukiBridgeDiscovery.DiscoverAsync(httpClient, CancellationToken.None);

        // Assert
        Assert.Equal(["192.168.1.50:8080", "[fe80::1]:8081"], bridges.Select(bridge => bridge.HostAddress));
        Assert.Equal(2117604523, bridges[0].BridgeId);
        Assert.Equal(new Uri(NukiBridgeDiscovery.DiscoveryAddress), handler.RequestUri);
    }

    [Fact]
    public async Task WhenTheDiscoveryResponseIsLargerThanTheLimit_ThenTheDiscoveryFails()
    {
        // Arrange
        using var httpClient = new HttpClient(new JsonHandler("{" + new string(' ', NukiBridgeClient.MaxResponseSize) + "}"));

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => NukiBridgeDiscovery.DiscoverAsync(httpClient, CancellationToken.None));
    }

    [Fact]
    public async Task WhenDiscoveryServiceDoesNotAnswerInTime_ThenATimeoutExceptionIsThrown()
    {
        // Arrange
        using var httpClient = new HttpClient(new NeverAnsweringHandler());

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(
            () => NukiBridgeDiscovery.DiscoverAsync(httpClient, TimeSpan.FromMilliseconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task WhenCallerCancelsTheDiscovery_ThenTheCancellationIsThrown()
    {
        // Arrange
        using var httpClient = new HttpClient(new NeverAnsweringHandler());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NukiBridgeDiscovery.DiscoverAsync(httpClient, TimeSpan.FromHours(1), cancellation.Token));
    }

    [Theory]
    [InlineData("192.168.1.50", "192.168.1.50:8080")]
    [InlineData("fe80::1", "[fe80::1]:8080")]
    public void WhenBridgeIsDiscovered_ThenItsHostAddressCarriesThePort(string ipAddress, string expectedHostAddress)
    {
        // Act
        var bridge = new NukiDiscoveredBridge(1, ipAddress, 8080);

        // Assert
        Assert.Equal(expectedHostAddress, bridge.HostAddress);
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

    [Theory]
    [InlineData(NukiFixtures.SoftwareInfo, NukiTokenMode.Plain)]
    [InlineData(NukiFixtures.Info, NukiTokenMode.Encrypted)]
    public async Task WhenBridgeRejectsTheEncryptedToken_ThenItsBridgeTypeReadWithThePlainTokenDecides(string infoJson, NukiTokenMode expected)
    {
        // Arrange
        var fake = new FakeNukiBridge { InfoJson = infoJson };
        fake.StatusOverride = _ => fake.CountRequests("info") == 1 ? HttpStatusCode.Unauthorized : null;

        // Act
        var mode = await NukiBridgeDiscovery.DetectTokenModeAsync(fake.CreateClient(string.Empty), "192.168.1.50", "abc123", CancellationToken.None);

        // Assert
        Assert.Equal(expected, mode);
        Assert.Contains("token=abc123", fake.Requests[1].Query);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NeverAnsweringHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Not reached.");
        }
    }
}
