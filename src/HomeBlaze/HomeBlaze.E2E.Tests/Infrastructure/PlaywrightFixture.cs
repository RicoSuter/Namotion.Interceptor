using HomeBlaze.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.Plugins;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// xUnit collection fixture that manages Playwright browser and the test server.
/// Shared across all tests in the same collection for efficiency.
/// </summary>
public class PlaywrightFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private WebTestingHostFactory<App>? _factory;
    private IBrowserContext? _context;

    public IBrowser Browser => _browser ?? throw new InvalidOperationException("Browser not initialized");

    public string ServerAddress => _factory?.ServerAddress ?? throw new InvalidOperationException("Server not started");

    public async Task InitializeAsync()
    {
        // Start the test server with Kestrel
        _factory = new WebTestingHostFactory<App>();
        // Force server to start by accessing ServerAddress which calls EnsureServer
        var address = _factory.ServerAddress;
        Console.WriteLine($"Test server started at: {address}");

        // The root subject is loaded by a background service, so the server serves requests before the
        // object graph behind them exists. The UI waits that window out on its own, but the wait would
        // otherwise land inside the first test's own timeout instead of here.
        var rootManager = _factory.ServerServices.GetRequiredService<RootManager>();
        try
        {
            await rootManager.RootLoaded.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("The root subject was not loaded before the tests started", exception);
        }

        // Initialize Playwright and launch browser
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });
    }

    public async Task DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.CloseAsync();
            _context = null;
        }

        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>
    /// Waits until the plugin provider in the subject tree has loaded every plugin it lists and all of them are running.
    /// </summary>
    public async Task WaitForPluginsLoadedAsync()
    {
        var factory = _factory ?? throw new InvalidOperationException("Server not started");
        var root = await factory.ServerServices.GetRequiredService<RootManager>().RootLoaded;
        var registry = root.Context.GetService<ISubjectRegistry>();

        // Plugins load in the background after startup and may first download dependencies from nuget.org.
        await AsyncTestHelpers.WaitUntilAsync(
            () => registry.KnownSubjects.Keys.OfType<NuGetPluginProvider>().FirstOrDefault() is { } provider &&
                provider.Plugins.Length > 0 &&
                provider.LoadedPlugins.Count == provider.Plugins.Length &&
                provider.LoadedPlugins.Values.All(plugin => plugin.Status == ServiceStatus.Running),
            timeout: TimeSpan.FromMinutes(2),
            message: "The plugins in the test data were not all loaded and running");
    }

    /// <summary>
    /// Creates a new browser context and page for isolated test execution.
    /// Closes the previous test's context, so only one is ever open.
    /// </summary>
    public async Task<IPage> CreatePageAsync()
    {
        // An open context keeps its page's Blazor circuit alive, and the host renders every connected
        // circuit on each state change. Holding one per test for the whole run left the last tests
        // contending with two dozen idle circuits, which was enough on a two core runner for a freshly
        // loaded page to miss the clicks sent to it. Tests here run one at a time and take one page
        // each, so the previous context is finished with by the time the next one is asked for.
        if (_context is not null)
        {
            await _context.CloseAsync();
        }

        _context = await Browser.NewContextAsync();
        return await _context.NewPageAsync();
    }
}

/// <summary>
/// Collection definition for tests that share the PlaywrightFixture.
/// </summary>
[CollectionDefinition(nameof(PlaywrightCollection))]
public class PlaywrightCollection : ICollectionFixture<PlaywrightFixture>
{
}
