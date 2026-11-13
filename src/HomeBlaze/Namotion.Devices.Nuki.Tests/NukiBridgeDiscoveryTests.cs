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
        using var httpClient = CreateClient(json);

        // Act
        var bridges = await NukiBridgeDiscovery.DiscoverAsync(httpClient, CancellationToken.None);

        // Assert
        Assert.Equal(["192.168.1.50:8080", "[fe80::1]:8081"], bridges.Select(bridge => bridge.HostAddress));
        Assert.Equal(2117604523, bridges[0].BridgeId);
    }

    [Fact]
    public async Task WhenTheDiscoveryResponseIsLargerThanTheLimit_ThenTheDiscoveryFails()
    {
        // Arrange
        using var httpClient = CreateClient("{" + new string(' ', NukiBridgeClient.MaxResponseSize) + "}");

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => NukiBridgeDiscovery.DiscoverAsync(httpClient, CancellationToken.None));
    }

    [Fact]
    public async Task WhenDiscoveryServiceDoesNotAnswerInTime_ThenATimeoutExceptionIsThrown()
    {
        // Arrange
        using var httpClient = CreateNeverAnsweringClient();

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(
            () => NukiBridgeDiscovery.DiscoverAsync(httpClient, TimeSpan.FromMilliseconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task WhenCallerCancelsTheDiscovery_ThenTheCancellationIsThrown()
    {
        // Arrange
        using var httpClient = CreateNeverAnsweringClient();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NukiBridgeDiscovery.DiscoverAsync(httpClient, TimeSpan.FromHours(1), cancellation.Token));
    }

    [Fact]
    public async Task WhenBridgeConfirmsThePairing_ThenItsTokenIsReturned()
    {
        // Arrange
        var fake = new FakeNukiBridge();

        // Act
        var token = await NukiBridgeDiscovery.PairAsync(fake.CreateClient(), "192.168.1.50", CancellationToken.None);

        // Assert
        Assert.Equal("abc123", token);
        Assert.Equal("http://192.168.1.50:8080/auth", Assert.Single(fake.Requests).ToString());
    }

    [Fact]
    public async Task WhenButtonIsNotPressed_ThenPairingReturnsNull()
    {
        // Arrange
        var fake = new FakeNukiBridge { AuthJson = """{"success": false}""" };

        // Act
        var token = await NukiBridgeDiscovery.PairAsync(fake.CreateClient(), "192.168.1.50", CancellationToken.None);

        // Assert
        Assert.Null(token);
    }

    [Fact]
    public async Task WhenBridgeAcceptsTheEncryptedToken_ThenTheTokenModeIsEncryptedWithoutWarning()
    {
        // Arrange
        var fake = new FakeNukiBridge();

        // Act
        var detection = await NukiBridgeDiscovery.DetectTokenModeAsync(fake.CreateClient(), "192.168.1.50", "abc123", CancellationToken.None);

        // Assert
        Assert.Equal(new NukiTokenModeDetection(NukiTokenMode.Encrypted, Warning: null), detection);
        Assert.Single(fake.Requests);
    }

    [Theory]
    [InlineData(NukiFixtures.SoftwareInfo, NukiTokenMode.Plain, null)]
    [InlineData(NukiFixtures.Info, NukiTokenMode.Encrypted, "clock")]
    public async Task WhenBridgeRejectsTheEncryptedToken_ThenItsBridgeTypeReadWithThePlainTokenDecides(
        string infoJson, NukiTokenMode expectedMode, string? expectedWarning)
    {
        // Arrange
        var fake = new FakeNukiBridge { InfoJson = infoJson };
        fake.StatusOverride = _ => fake.CountRequests("info") == 1 ? HttpStatusCode.Unauthorized : null;

        // Act
        var detection = await NukiBridgeDiscovery.DetectTokenModeAsync(fake.CreateClient(), "192.168.1.50", "abc123", CancellationToken.None);

        // Assert
        Assert.Equal(expectedMode, detection.Mode);
        if (expectedWarning is null)
        {
            Assert.Null(detection.Warning);
        }
        else
        {
            Assert.Contains(expectedWarning, detection.Warning);
        }

        Assert.Contains("token=abc123", fake.Requests[1].Query);
    }

    [Fact]
    public async Task WhenBridgeRejectsBothTokens_ThenTheTokenModeIsEncryptedWithAWarning()
    {
        // Arrange
        var fake = new FakeNukiBridge { StatusOverride = _ => HttpStatusCode.Unauthorized };

        // Act
        var detection = await NukiBridgeDiscovery.DetectTokenModeAsync(fake.CreateClient(), "192.168.1.50", "abc123", CancellationToken.None);

        // Assert
        Assert.Equal(NukiTokenMode.Encrypted, detection.Mode);
        Assert.Contains("could not be detected", detection.Warning);
        Assert.Equal(2, fake.CountRequests("info"));
    }

    private static HttpClient CreateClient(string json) => new(new StubHandler(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") })));

    private static HttpClient CreateNeverAnsweringClient() => new(new StubHandler(cancellationToken =>
        new TaskCompletionSource<HttpResponseMessage>().Task.WaitAsync(cancellationToken)));

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            answer(cancellationToken);
    }
}
