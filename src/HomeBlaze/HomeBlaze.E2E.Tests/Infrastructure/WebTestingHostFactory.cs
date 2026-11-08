using HomeBlaze.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    private readonly string _authenticationDatabasePath;

    private int _startedHostCount;

    /// <param name="testTypes">Subject and component types of the tests that the server has to know.</param>
    /// <param name="authenticationDatabasePath">The path of the authentication database, which the server creates and seeds when missing.</param>
    public WebTestingHostFactory(IReadOnlyCollection<Type> testTypes, string authenticationDatabasePath)
    {
        _testTypes = testTypes;
        _authenticationDatabasePath = authenticationDatabasePath;
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

        // Outside the test data, so that every server starts with a database of its own, seeded with the administrator.
        builder.UseSetting("Authentication:Database", _authenticationDatabasePath);

        // The browser contexts of the tests name a client address of their own in X-Forwarded-For, so that the rate limits
        // per address of signing in and of starting the app count each context as the separate client it stands for.
        builder.UseSetting("Authentication:ForwardedHeadersKnownProxies:0", "127.0.0.1");

        // The serializer of the configuration reads the types once, the first time it reads a configuration, which the
        // hosted service that loads the root does. So the types of the tests are added by a hosted service that starts
        // before every other one: added after the start of the server, they are missed whenever the root loads first.
        builder.ConfigureServices(services => services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(
            provider => new TestTypeRegistration(this, provider.GetRequiredService<TypeProvider>()))));

        builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(ServerLog));
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
}
