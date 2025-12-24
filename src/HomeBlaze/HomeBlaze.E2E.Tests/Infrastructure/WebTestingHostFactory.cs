using HomeBlaze.Components;
using HomeBlaze.Samples;
using HomeBlaze.Servers.OpcUa;
using HomeBlaze.Servers.OpcUa.Blazor;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Blazor.Files;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// A custom WebApplicationFactory that uses Kestrel instead of TestServer.
/// Playwright requires a real HTTP endpoint to connect to.
/// Based on: https://danieldonbavand.com/2022/06/13/using-playwright-with-the-webapplicationfactory-to-test-a-blazor-application/
/// </summary>
/// <typeparam name="TProgram">The entry point class (usually Program)</typeparam>
public class WebTestingHostFactory<TProgram> : WebApplicationFactory<TProgram>
    where TProgram : class
{
    private IHost? _kestrelHost;
    private string? _serverAddress;

    public string ServerAddress
    {
        get
        {
            EnsureServer();
            return _serverAddress!;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseUrls("http://127.0.0.1:0");
        builder.UseEnvironment("Development");

        // Use test-specific root configuration to avoid loading HomeBlaze's Data folder
        builder.UseSetting("HomeBlaze:RootConfigFile", "testRoot.json");
    }

    private void EnsureServer()
    {
        if (_serverAddress is null)
        {
            // Force the server to start by accessing Services
            _ = Services;
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Create the standard TestServer host (required by base class)
        var testHost = builder.Build();

        // Reconfigure the builder to use Kestrel instead
        builder.ConfigureWebHost(webHostBuilder =>
            webHostBuilder.UseKestrel());

        // Build and start the Kestrel host
        _kestrelHost = builder.Build();

        // Configure TypeProvider with essential assemblies for E2E tests
        var typeProvider = _kestrelHost.Services.GetRequiredService<TypeProvider>();
        typeProvider
            .AddAssembly(typeof(FluentStorageContainer).Assembly)      // HomeBlaze.Storage
            .AddAssembly(typeof(Motor).Assembly);                      // HomeBlaze.Samples (for test subjects)

        _kestrelHost.Start();

        // Get the address from Kestrel
        var server = _kestrelHost.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>();

        _serverAddress = addresses!.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Could not determine Kestrel server address");

        if (!_serverAddress.EndsWith('/'))
            _serverAddress += '/';

        // Start the TestServer host to satisfy the base class
        testHost.Start();
        return testHost;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Give the host time to stop gracefully, including OPC UA server shutdown
            try
            {
                _kestrelHost?.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore timeout exceptions during shutdown
            }
            _kestrelHost?.Dispose();
        }
        base.Dispose(disposing);
    }
}
