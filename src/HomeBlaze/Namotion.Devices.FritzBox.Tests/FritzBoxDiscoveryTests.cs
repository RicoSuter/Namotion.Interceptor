using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
        using var httpClient = CreateHttpClient();

        // Act
        var result = await FritzBoxDiscovery.DescribeAsync(httpClient, "127.0.0.1", fake.HttpPort, CancellationToken.None);

        // Assert
        Assert.Equal(new FritzBoxDiscoveryResult("127.0.0.1", "FRITZ!Box 7590", "FRITZ!Box 7590", "uuid:75802409-bccb-40e7-8e6c-0123456789ab", IsRepeater: false), result);
    }

    [Fact]
    public async Task WhenTheDescriptionListsNoInternetConnection_ThenTheDeviceIsARepeater()
    {
        // Arrange: the services a FRITZ!Repeater 1200 AX lists.
        await using var fake = await FakeFritzBox.StartAsync();
        fake.DescriptionXml =
            "<root xmlns=\"urn:dslforum-org:device-1-0\"><device><friendlyName>Repeater</friendlyName><modelName>FRITZ!Repeater 1200 AX</modelName><serviceList>" +
            "<service><serviceType>urn:dslforum-org:service:DeviceInfo:1</serviceType><controlURL>/upnp/control/deviceinfo</controlURL></service>" +
            "<service><serviceType>urn:dslforum-org:service:Hosts:1</serviceType><controlURL>/upnp/control/hosts</controlURL></service>" +
            "</serviceList></device></root>";
        using var httpClient = CreateHttpClient();

        // Act
        var result = await FritzBoxDiscovery.DescribeAsync(httpClient, "127.0.0.1", fake.HttpPort, CancellationToken.None);

        // Assert
        Assert.True(result!.IsRepeater);
        Assert.Equal("FRITZ!Repeater 1200 AX", result.Model);
    }

    [Fact]
    public async Task WhenTheRouterDoesNotAnswer_ThenItIsSkipped()
    {
        // Arrange
        using var httpClient = CreateHttpClient();

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
        using var httpClient = CreateHttpClient();

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
        using var httpClient = CreateHttpClient();

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
        using var httpClient = CreateHttpClient();

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
        using var httpClient = CreateHttpClient();
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FritzBoxDiscovery.DescribeAsync(httpClient, "127.0.0.1", fake.HttpPort, cancellationTokenSource.Token));
    }

    [Theory]
    [InlineData("192.168.178.1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.178.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("212.42.244.122", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("2a00:1450::1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("172.15.255.255", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", true)]
    [InlineData("::ffff:212.42.244.122", false)]
    public void WhenAnAddressIsChecked_ThenOnlyLocalNetworkAddressesCount(string address, bool isLocal)
    {
        // Act
        var result = FritzBoxDiscovery.IsLocalNetworkAddress(IPAddress.Parse(address));

        // Assert
        Assert.Equal(isLocal, result);
    }

    [Fact]
    public async Task WhenTheNameResolvesOnlyToPublicAddresses_ThenItIsNotOnTheLocalNetwork()
    {
        // Act
        var result = await FritzBoxDiscovery.IsOnLocalNetworkAsync(
            "fritz.box", (_, _) => Task.FromResult(new[] { IPAddress.Parse("212.42.244.122") }), TimeSpan.FromSeconds(5), TimeProvider.System, CancellationToken.None);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task WhenTheNameResolvesToALocalAddress_ThenItIsOnTheLocalNetwork()
    {
        // Act
        var result = await FritzBoxDiscovery.IsOnLocalNetworkAsync(
            "fritz.box",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("2a00:1450::1"), IPAddress.Parse("192.168.178.1") }),
            TimeSpan.FromSeconds(5),
            TimeProvider.System,
            CancellationToken.None);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task WhenTheNameDoesNotResolve_ThenItIsNotOnTheLocalNetwork()
    {
        // Act
        var result = await FritzBoxDiscovery.IsOnLocalNetworkAsync(
            "fritz.box", (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)), TimeSpan.FromSeconds(5), TimeProvider.System, CancellationToken.None);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task WhenTheNameResolutionTimesOut_ThenItIsNotOnTheLocalNetwork()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var timeout = TimeSpan.FromSeconds(3);

        // Act
        var checking = FritzBoxDiscovery.IsOnLocalNetworkAsync("fritz.box", ResolveNeverAsync, timeout, timeProvider, CancellationToken.None);
        timeProvider.Advance(timeout);
        var result = await checking;

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task WhenTheCallerCanceled_ThenTheCheckIsCanceled()
    {
        // Arrange
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FritzBoxDiscovery.IsOnLocalNetworkAsync(
            "fritz.box", ResolveNeverAsync, TimeSpan.FromSeconds(3), new FakeTimeProvider(), cancellationTokenSource.Token));
    }

    [Fact]
    public async Task WhenTheNameResolvesOnlyToPublicAddresses_ThenItIsNotDescribed()
    {
        // Arrange
        var describedHosts = new ConcurrentQueue<string>();

        // Act
        await SearchRecordingDescriptionsAsync(new EmptySsdpSearch(), IPAddress.Parse("212.42.244.122"), describedHosts);

        // Assert
        Assert.Empty(describedHosts);
    }

    [Fact]
    public async Task WhenTheNameResolvesToALocalAddress_ThenItIsDescribed()
    {
        // Arrange
        var describedHosts = new ConcurrentQueue<string>();

        // Act
        await SearchRecordingDescriptionsAsync(new EmptySsdpSearch(), IPAddress.Parse("192.168.178.1"), describedHosts);

        // Assert
        Assert.Equal(["fritz.box"], describedHosts);
    }

    [Fact]
    public async Task WhenTheNameIsNotOnTheLocalNetwork_ThenAHostFoundOverSsdpIsStillDescribed()
    {
        // Arrange
        var describedHosts = new ConcurrentQueue<string>();

        // Act
        await SearchRecordingDescriptionsAsync(new SingleSsdpSearch("192.168.178.1"), IPAddress.Parse("212.42.244.122"), describedHosts);

        // Assert
        Assert.Equal(["192.168.178.1"], describedHosts);
    }

    [Fact]
    public void WhenTheUdnsMatch_ThenARouterAtAnotherAddressIsTheSame()
    {
        // Act & Assert
        Assert.True(FritzBoxDiscovery.IsSameRouter(CreateRouter("192.168.178.1", "uuid:a"), Found("192.168.178.2", "UUID:A")));
    }

    [Fact]
    public void WhenTheHostAddressesMatch_ThenARouterWithAnotherUdnIsTheSame()
    {
        // Act & Assert
        Assert.True(FritzBoxDiscovery.IsSameRouter(CreateRouter("192.168.178.1", "uuid:a"), Found("192.168.178.1", "uuid:b")));
    }

    [Fact]
    public void WhenNeitherTheUdnNorTheHostAddressMatches_ThenTheRouterIsAnother()
    {
        // Act & Assert
        Assert.False(FritzBoxDiscovery.IsSameRouter(CreateRouter("192.168.178.1", "uuid:a"), Found("192.168.178.2", "uuid:b")));
        Assert.False(FritzBoxDiscovery.IsSameRouter(CreateRouter("192.168.178.1", "uuid:a"), Found("192.168.178.2", null)));
    }

    [Fact]
    public void WhenTheRouterHasNoUdn_ThenTheHostAddressDecides()
    {
        // Act & Assert
        Assert.True(FritzBoxDiscovery.IsSameRouter(CreateRouter(" Fritz.Box ", null), Found("fritz.box", "uuid:a")));
        Assert.False(FritzBoxDiscovery.IsSameRouter(CreateRouter("192.168.178.1", null), Found("192.168.178.2", "uuid:a")));
    }

    [Fact]
    public void WhenAResultDescribesARepeater_ThenItsDetailSaysSo()
    {
        // Arrange
        var result = new FritzBoxDiscoveryResult("192.168.178.3", null, "FRITZ!Repeater 1200", "uuid:r", IsRepeater: true);

        // Act & Assert
        Assert.Equal("uuid:r", result.Key);
        Assert.Equal("FRITZ!Repeater 1200", result.DisplayName);
        Assert.Equal("192.168.178.3 (repeater)", result.Detail);
    }

    [Fact]
    public void WhenOneRouterAndRepeatersAreFound_ThenTheRepeatersAreIncludedItemsOfTheRouter()
    {
        // Arrange
        var router = Found("192.168.178.1", "uuid:router");
        FritzBoxDiscoveryResult[] results =
        [
            router,
            new("192.168.178.4", "Repeater Office", "FRITZ!Repeater 1200 AX", "uuid:office", IsRepeater: true),
            new("192.168.178.3", null, "FRITZ!Repeater 600", "uuid:attic", IsRepeater: true)
        ];

        // Act
        var included = FritzBoxDiscovery.IncludeRepeaters(results);

        // Assert
        var single = Assert.Single(included);
        Assert.Equal(router.Key, single.Key);
        Assert.Equal("192.168.178.1", single.Detail);
        Assert.Equal(
            [new DiscoveredItem("FRITZ!Repeater 600", "192.168.178.3"), new DiscoveredItem("Repeater Office", "192.168.178.4")],
            single.IncludedItems);
    }

    [Fact]
    public void WhenTwoRoutersAreFound_ThenTheRepeatersStayResultsOfTheirOwn()
    {
        // Arrange
        FritzBoxDiscoveryResult[] results =
        [
            Found("192.168.178.1", "uuid:first"),
            Found("192.168.179.1", "uuid:second"),
            new("192.168.178.3", null, "FRITZ!Repeater 600", "uuid:repeater", IsRepeater: true)
        ];

        // Act
        var included = FritzBoxDiscovery.IncludeRepeaters(results);

        // Assert
        Assert.Equal(results, included);
        Assert.All(included, result => Assert.Empty(result.IncludedItems));
    }

    [Fact]
    public void WhenOnlyARepeaterIsFound_ThenItStaysAResultOfItsOwn()
    {
        // Arrange
        FritzBoxDiscoveryResult[] results = [new("192.168.178.3", null, "FRITZ!Repeater 600", "uuid:repeater", IsRepeater: true)];

        // Act
        var included = FritzBoxDiscovery.IncludeRepeaters(results);

        // Assert
        Assert.Equal(results, included);
    }

    [Fact]
    public void WhenOnlyARouterIsFound_ThenItIncludesNothing()
    {
        // Arrange
        FritzBoxDiscoveryResult[] results = [Found("192.168.178.1", "uuid:router")];

        // Act
        var included = FritzBoxDiscovery.IncludeRepeaters(results);

        // Assert
        Assert.Empty(Assert.Single(included).IncludedItems);
    }

    private static FritzBox CreateRouter(string? hostAddress, string? udn) =>
        new(NullLogger<FritzBox>.Instance) { HostAddress = hostAddress, Udn = udn };

    private static FritzBoxDiscoveryResult Found(string hostAddress, string? udn) =>
        new(hostAddress, "FRITZ!Box 7590", "FRITZ!Box 7590", udn, IsRepeater: false);

    private static Task<IReadOnlyList<FritzBoxDiscoveryResult>> SearchRecordingDescriptionsAsync(
        ISsdpSearch ssdpSearch, IPAddress wellKnownHostAddress, ConcurrentQueue<string> describedHosts) =>
        FritzBoxDiscovery.SearchAsync(
            ssdpSearch,
            TimeSpan.FromSeconds(5),
            (_, _) => Task.FromResult(new[] { wellKnownHostAddress }),
            (host, _) =>
            {
                describedHosts.Enqueue(host);
                return Task.FromResult<FritzBoxDiscoveryResult?>(null);
            },
            TimeProvider.System,
            CancellationToken.None);

    private static async Task<IPAddress[]> ResolveNeverAsync(string host, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return [];
    }

    /// <summary>
    /// Builds the client the way <see cref="FritzBoxDiscovery.SearchAsync(ISsdpSearch, TimeSpan, CancellationToken)"/>
    /// does, so tests exercise the real discovery settings, including the size cap that the oversized-description test
    /// relies on.
    /// </summary>
    private static HttpClient CreateHttpClient() =>
        FritzBoxHttpClient.CreatePlain(TimeSpan.FromSeconds(5), Tr64Description.MaximumBytes);

    private sealed class EmptySsdpSearch : ISsdpSearch
    {
        public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SsdpResponse>>([]);
    }

    private sealed class SingleSsdpSearch(string host) : ISsdpSearch
    {
        public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SsdpResponse>>(
                [new SsdpResponse(new Uri($"http://{host}:49000/tr64desc.xml"), UniqueServiceName: null, new Dictionary<string, string>())]);
    }
}
