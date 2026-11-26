using System.Net;
using System.Text;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Nuki.Client;
using Namotion.Devices.Nuki.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiBridgeDiscoveryTests
{
    private const string OneBridgeJson = """{"bridges": [{"bridgeId": 2117604523, "ip": "192.0.2.50", "port": 8080}], "errorCode": 0}""";

    [Fact]
    public async Task WhenDiscoveryServiceListsBridges_ThenOnlyBridgesWithAnIpAddressAndAValidPortAreReturned()
    {
        // Arrange
        const string json = """
            {"bridges": [
              {"bridgeId": 2117604523, "ip": "192.0.2.50", "port": 8080, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 7, "ip": "fe80::1", "port": 8081, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 42, "ip": "0.0.0.0", "port": 0, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 43, "ip": "192.0.2.51/path", "port": 8080, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 44, "ip": "nuki.local", "port": 8080, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 45, "ip": "192.0.2.52", "port": 65536, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 46, "ip": "192.0.2.53", "port": -1, "dateUpdated": "2017-06-14T06:53:44Z"},
              {"bridgeId": 47, "port": 8080, "dateUpdated": "2017-06-14T06:53:44Z"}],
             "errorCode": 0}
            """;
        using var httpClient = CreateAnsweringClient(json);

        // Act
        var bridges = await NukiBridgeDiscovery.DiscoverAsync(httpClient, CancellationToken.None);

        // Assert
        Assert.Equal(["192.0.2.50:8080", "[fe80::1]:8081"], bridges.Select(bridge => bridge.HostAddress));
        Assert.Equal(2117604523, bridges[0].BridgeId);
    }

    [Fact]
    public async Task WhenTheDiscoveryResponseIsLargerThanTheLimit_ThenTheDiscoveryFails()
    {
        // Arrange
        using var httpClient = CreateAnsweringClient("{" + new string(' ', NukiBridgeClient.MaxResponseSize) + "}");

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
        var token = await NukiBridgeDiscovery.PairAsync(fake.CreateClient(), "192.0.2.50", CancellationToken.None);

        // Assert
        Assert.Equal("abc123", token);
        Assert.Equal("http://192.0.2.50:8080/auth", Assert.Single(fake.Requests).ToString());
    }

    [Fact]
    public async Task WhenButtonIsNotPressed_ThenPairingReturnsNull()
    {
        // Arrange
        var fake = new FakeNukiBridge { AuthJson = """{"success": false}""" };

        // Act
        var token = await NukiBridgeDiscovery.PairAsync(fake.CreateClient(), "192.0.2.50", CancellationToken.None);

        // Assert
        Assert.Null(token);
    }

    [Fact]
    public async Task WhenBridgeAcceptsTheEncryptedToken_ThenTheTokenModeIsEncryptedWithoutWarning()
    {
        // Arrange
        var fake = new FakeNukiBridge();

        // Act
        var detection = await NukiBridgeDiscovery.DetectTokenModeAsync(fake.CreateClient(), "192.0.2.50", "abc123", CancellationToken.None);

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
        var detection = await NukiBridgeDiscovery.DetectTokenModeAsync(fake.CreateClient(), "192.0.2.50", "abc123", CancellationToken.None);

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
        var detection = await NukiBridgeDiscovery.DetectTokenModeAsync(fake.CreateClient(), "192.0.2.50", "abc123", CancellationToken.None);

        // Assert
        Assert.Equal(NukiTokenMode.Encrypted, detection.Mode);
        Assert.Contains("could not be detected", detection.Warning);
        Assert.Equal(2, fake.CountRequests("info"));
    }

    [Theory]
    [InlineData("192.0.2.50", true)]
    [InlineData("192.0.2.50:8080", true)]
    [InlineData("http://192.0.2.50:8080", true)]
    [InlineData("192.0.2.50:8081", false)]
    [InlineData("192.0.2.51", false)]
    [InlineData(null, false)]
    public void WhenHostAddressesAreCompared_ThenHostAndPortDecide(string? hostAddress, bool expected)
    {
        // Arrange
        var result = new NukiDiscoveredBridge(2117604523, "192.0.2.50:8080");
        using var bridge = new NukiBridge(NullLogger<NukiBridge>.Instance) { HostAddress = hostAddress };

        // Act
        var isSame = result.Matches(bridge);

        // Assert
        Assert.Equal(expected, isSame);
    }

    [Fact]
    public async Task WhenTheDiscoveryServiceListsABridge_ThenItIsFound()
    {
        // Arrange
        const string json = """{"bridges": [{"bridgeId": 2117604523, "ip": "192.0.2.50", "port": 8080}], "errorCode": 0}""";
        ISubjectDiscovery discovery = new NukiBridgeDiscovery(new StubHttpClientFactory(() => CreateAnsweringClient(json)), TimeProvider.System);

        // Act
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        var bridge = Assert.IsType<NukiDiscoveredBridge>(Assert.Single(results));
        Assert.Equal("2117604523", bridge.Key);
        Assert.Equal("Nuki Bridge 2117604523", bridge.DisplayName);
        Assert.Equal("192.0.2.50:8080", bridge.Detail);
    }

    [Fact]
    public async Task WhenTheDiscoveryServiceCannotBeReached_ThenTheSubjectDiscoveryFindsNothing()
    {
        // Arrange
        ISubjectDiscovery discovery = new NukiBridgeDiscovery(new StubHttpClientFactory(() => new HttpClient(new StubHandler(_ =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("No route to host"))))), TimeProvider.System);

        // Act
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task WhenTheDiscoveryServiceAnswersWithAnError_ThenTheSubjectDiscoveryFindsNothing()
    {
        // Arrange
        ISubjectDiscovery discovery = new NukiBridgeDiscovery(new StubHttpClientFactory(() => new HttpClient(new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))), TimeProvider.System);

        // Act
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task WhenTheDiscoveryServiceDoesNotAnswerInTime_ThenTheSubjectDiscoveryFindsNothing()
    {
        // Arrange
        ISubjectDiscovery discovery = new NukiBridgeDiscovery(new StubHttpClientFactory(CreateNeverAnsweringClient), TimeProvider.System)
        {
            Timeout = TimeSpan.FromMilliseconds(1)
        };

        // Act
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task WhenTheCallerCancelsTheSubjectDiscovery_ThenTheCancellationIsThrown()
    {
        // Arrange
        ISubjectDiscovery discovery = new NukiBridgeDiscovery(new StubHttpClientFactory(CreateNeverAnsweringClient), TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await discovery.DiscoverAsync(cancellation.Token).ToListAsync());
    }

    [Fact]
    public async Task WhenTheDiscoveryServiceAnswersWithInvalidJson_ThenTheSubjectDiscoveryFails()
    {
        // Arrange
        ISubjectDiscovery discovery = new NukiBridgeDiscovery(new StubHttpClientFactory(() => CreateAnsweringClient("{\"bridges\": 42}")), TimeProvider.System);

        // Act & Assert
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            async () => await discovery.DiscoverAsync(CancellationToken.None).ToListAsync());
    }

    [Fact]
    public async Task WhenAScanFollowsWithinAMinute_ThenTheDiscoveryServiceIsNotAskedAgain()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var requestCount = 0;
        ISubjectDiscovery discovery = new NukiBridgeDiscovery(new StubHttpClientFactory(() => new HttpClient(new StubHandler(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(CreateJsonResponse(OneBridgeJson));
        }))), timeProvider);
        await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Act
        timeProvider.Advance(NukiBridgeDiscovery.CacheDuration - TimeSpan.FromSeconds(1));
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        Assert.Equal("2117604523", Assert.Single(results).Key);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task WhenAMinuteHasPassed_ThenTheDiscoveryServiceIsAskedAgain()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var requestCount = 0;
        ISubjectDiscovery discovery = new NukiBridgeDiscovery(new StubHttpClientFactory(() => new HttpClient(new StubHandler(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(CreateJsonResponse(OneBridgeJson));
        }))), timeProvider);
        await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Act
        timeProvider.Advance(NukiBridgeDiscovery.CacheDuration);
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        Assert.Single(results);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task WhenTheDiscoveryServiceAnsweredWithAnError_ThenNothingIsFoundWithoutAskingAgainWithinAMinute()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var requestCount = 0;
        ISubjectDiscovery discovery = new NukiBridgeDiscovery(new StubHttpClientFactory(() => new HttpClient(new StubHandler(_ =>
            Task.FromResult(Interlocked.Increment(ref requestCount) == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : CreateJsonResponse(OneBridgeJson))))), timeProvider);
        await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Act
        timeProvider.Advance(NukiBridgeDiscovery.CacheDuration - TimeSpan.FromSeconds(1));
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        Assert.Empty(results);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task WhenTheDiscoveryServiceCouldNotBeReached_ThenTheNextScanAsksAgain()
    {
        // Arrange
        var requestCount = 0;
        ISubjectDiscovery discovery = new NukiBridgeDiscovery(new StubHttpClientFactory(() => new HttpClient(new StubHandler(_ =>
            Interlocked.Increment(ref requestCount) == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("No route to host"))
                : Task.FromResult(CreateJsonResponse(OneBridgeJson))))), new FakeTimeProvider());
        var unreachedResults = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Act
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        Assert.Empty(unreachedResults);
        Assert.Single(results);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task WhenALookupIsCanceled_ThenTheNextScanAsksTheDiscoveryServiceAgain()
    {
        // Arrange
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        ISubjectDiscovery discovery = new NukiBridgeDiscovery(new StubHttpClientFactory(() => new HttpClient(new StubHandler(cancellationToken =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                requested.SetResult();
                return new TaskCompletionSource<HttpResponseMessage>().Task.WaitAsync(cancellationToken);
            }

            return Task.FromResult(CreateJsonResponse(OneBridgeJson));
        }))), new FakeTimeProvider());
        using var cancellation = new CancellationTokenSource();
        var canceledLookup = discovery.DiscoverAsync(cancellation.Token).ToListAsync().AsTask();
        await requested.Task.WaitAsync(TestTimeouts.Default);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledLookup.WaitAsync(TestTimeouts.Default));

        // Act
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        Assert.Single(results);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task WhenALookupFails_ThenTheNextScanAsksTheDiscoveryServiceAgain()
    {
        // Arrange
        var requestCount = 0;
        ISubjectDiscovery discovery = new NukiBridgeDiscovery(new StubHttpClientFactory(() => new HttpClient(new StubHandler(_ =>
            Task.FromResult(CreateJsonResponse(Interlocked.Increment(ref requestCount) == 1 ? "{\"bridges\": 42}" : OneBridgeJson))))),
            new FakeTimeProvider());
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            async () => await discovery.DiscoverAsync(CancellationToken.None).ToListAsync());

        // Act
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        Assert.Single(results);
        Assert.Equal(2, requestCount);
    }

    private static HttpResponseMessage CreateJsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpClient CreateAnsweringClient(string json) => new(new StubHandler(_ => Task.FromResult(CreateJsonResponse(json))));

    private static HttpClient CreateNeverAnsweringClient() => new(new StubHandler(cancellationToken =>
        new TaskCompletionSource<HttpResponseMessage>().Task.WaitAsync(cancellationToken)));

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            answer(cancellationToken);
    }

    private sealed class StubHttpClientFactory(Func<HttpClient> createClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => createClient();
    }
}
