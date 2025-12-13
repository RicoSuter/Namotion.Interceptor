using HomeBlaze.Components;
using Microsoft.Playwright;

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

    public IBrowser Browser => _browser ?? throw new InvalidOperationException("Browser not initialized");
    
    public string ServerAddress => _factory?.ServerAddress ?? throw new InvalidOperationException("Server not started");

    public async Task InitializeAsync()
    {
        // Start the test server with Kestrel
        _factory = new WebTestingHostFactory<App>();
        // Force server to start by accessing ServerAddress which calls EnsureServer
        var address = _factory.ServerAddress;
        Console.WriteLine($"Test server started at: {address}");

        // Initialize Playwright and launch browser
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            #if DEBUG
            Headless = false,
            #else
            Headless = true,
            #endif
            SlowMo = 500 // Slow down actions by 500ms for visibility
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>
    /// Creates a new browser context and page for isolated test execution.
    /// </summary>
    public async Task<IPage> CreatePageAsync()
    {
        var context = await Browser.NewContextAsync();
        return await context.NewPageAsync();
    }
}

/// <summary>
/// Collection definition for tests that share the PlaywrightFixture.
/// </summary>
[CollectionDefinition(nameof(PlaywrightCollection))]
public class PlaywrightCollection : ICollectionFixture<PlaywrightFixture>
{
}
