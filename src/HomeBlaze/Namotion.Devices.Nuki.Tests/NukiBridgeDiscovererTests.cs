using System.Net;
using System.Text;
using HomeBlaze.Abstractions.Discovery;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiBridgeDiscovererTests
{
    [Theory]
    [InlineData("192.168.1.50", true)]
    [InlineData("192.168.1.50:8080", true)]
    [InlineData("http://192.168.1.50:8080", true)]
    [InlineData("192.168.1.50:8081", false)]
    [InlineData("192.168.1.51", false)]
    [InlineData(null, false)]
    public void WhenHostAddressesAreCompared_ThenHostAndPortDecide(string? hostAddress, bool expected)
    {
        // Arrange
        var result = new NukiDiscoveredBridge(2117604523, "192.168.1.50:8080");

        // Act
        var isSame = NukiBridgeDiscoverer.IsSameBridge(hostAddress, result);

        // Assert
        Assert.Equal(expected, isSame);
    }

    [Fact]
    public async Task WhenTheDiscoveryServiceListsABridge_ThenItIsFound()
    {
        // Arrange
        const string json = """{"bridges": [{"bridgeId": 2117604523, "ip": "192.168.1.50", "port": 8080}], "errorCode": 0}""";
        ISubjectDiscoverer discoverer = new NukiBridgeDiscoverer(new StubHttpClientFactory(json));
        var scan = new DiscoveryScan(new EmptySsdpSearch(), new EmptyMulticastDnsBrowser(), new UnusedUdpDiscovery(), CancellationToken.None);

        // Act
        var discovered = await discoverer.DiscoverAsync(scan, [], CancellationToken.None).ToListAsync();

        // Assert
        var bridge = Assert.IsType<NukiDiscoveredBridge>(Assert.Single(discovered).Result);
        Assert.Equal("2117604523", bridge.Key);
        Assert.Equal("Nuki Bridge 2117604523", bridge.DisplayName);
        Assert.Equal("192.168.1.50:8080", bridge.Detail);
    }

    private sealed class StubHttpClientFactory(string json) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(json));
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private sealed class EmptySsdpSearch : ISsdpSearch
    {
        public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SsdpResponse>>([]);
    }

    private sealed class EmptyMulticastDnsBrowser : IMulticastDnsBrowser
    {
        public Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MulticastDnsService>>([]);
    }

    private sealed class UnusedUdpDiscovery : IUdpDiscovery
    {
        public IAsyncEnumerable<UdpDatagram> BroadcastAsync(int port, ReadOnlyMemory<byte> request, TimeSpan window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<UdpDatagram> ListenAsync(int port, TimeSpan window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
