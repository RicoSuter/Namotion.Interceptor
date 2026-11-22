using System.Net;
using System.Text;
using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Nuki;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> that runs the application in one Kestrel host on a free port,
/// because Playwright needs a real HTTP endpoint. <see cref="WebApplicationFactory{TEntryPoint}.Services"/> belongs to
/// that host.
/// </summary>
/// <typeparam name="TProgram">The entry point class (usually Program)</typeparam>
public class WebTestingHostFactory<TProgram> : WebApplicationFactory<TProgram>
    where TProgram : class
{
    private readonly IReadOnlyCollection<Type> _testTypes;

    private int _startedHostCount;

    /// <param name="testTypes">Subject and component types of the tests that the server has to know.</param>
    public WebTestingHostFactory(IReadOnlyCollection<Type> testTypes)
    {
        _testTypes = testTypes;
        UseKestrel(0);
    }

    /// <summary>
    /// The warnings and errors that the server logs.
    /// </summary>
    public ServerLogRecorder ServerLog { get; } = new();

    /// <summary>
    /// The base address of the server, with a trailing slash. Starts the server.
    /// </summary>
    public string ServerAddress
    {
        get
        {
            StartServer();
            return ClientOptions.BaseAddress.AbsoluteUri;
        }
    }

    /// <summary>
    /// The number of hosts of the application that this factory started.
    /// </summary>
    public int StartedHostCount => Volatile.Read(ref _startedHostCount);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Use test-specific root configuration to avoid loading HomeBlaze's Data folder
        builder.UseSetting("HomeBlaze:RootConfigFile", "testRoot.json");

        // Point to test-specific plugin configuration
        builder.UseSetting("PluginConfigurationPath", Path.Combine(AppContext.BaseDirectory, "TestData", "Plugins.json"));

        // The serializer of the configuration reads the types once, the first time it reads a configuration, which the
        // hosted service that loads the root does. So the types of the tests are added by a hosted service that starts
        // before every other one: added after the start of the server, they are missed whenever the root loads first.
        builder.ConfigureServices(services => services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(
            provider => new TestTypeRegistration(this, provider.GetRequiredService<TypeProvider>()))));

        builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(ServerLog));

        // A scan on the Discover page runs the discoverers of the application too, which must not search the network
        // of the machine, send broadcasts or bind its ports, or tell the Nuki discovery service its public IP address.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISsdpSearch>();
            services.AddSingleton<ISsdpSearch, EmptySsdpSearch>();
            services.RemoveAll<IMulticastDnsBrowser>();
            services.AddSingleton<IMulticastDnsBrowser, EmptyMulticastDnsBrowser>();
            services.RemoveAll<IUdpDiscovery>();
            services.AddSingleton<IUdpDiscovery, EmptyUdpDiscovery>();
            services.ConfigureHttpClientDefaults(httpClient => httpClient.AddHttpMessageHandler(() => new NukiDiscoveryStub()));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Set CWD to the test output directory so relative paths in test configuration
        // (testRoot.json, ./TestData) resolve correctly regardless of where dotnet test is run from.
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        return base.CreateHost(builder);
    }

    private sealed class TestTypeRegistration(WebTestingHostFactory<TProgram> factory, TypeProvider typeProvider) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref factory._startedHostCount);
            typeProvider.AddTypes(factory._testTypes);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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

    private sealed class EmptyUdpDiscovery : IUdpDiscovery
    {
        public IAsyncEnumerable<UdpDatagram> BroadcastAsync(int port, ReadOnlyMemory<byte> request, TimeSpan window, CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UdpDatagram>();

        public IAsyncEnumerable<UdpDatagram> ListenAsync(int port, TimeSpan window, CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UdpDatagram>();
    }

    /// <summary>
    /// Answers the Nuki discovery service with no bridges and passes every other request on.
    /// </summary>
    private sealed class NukiDiscoveryStub : DelegatingHandler
    {
        private static readonly string DiscoveryHost = new Uri(NukiBridgeDiscovery.DiscoveryAddress).Host;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (string.Equals(request.RequestUri?.Host, DiscoveryHost, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent("{\"bridges\":[]}", Encoding.UTF8, "application/json")
                });
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
