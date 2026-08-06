using HomeBlaze.Components;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// xUnit collection fixture that manages Playwright browser and the test server.
/// Shared across all tests in the same collection for efficiency.
/// </summary>
public class PlaywrightFixture : IAsyncLifetime
{
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(250);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private WebTestingHostFactory<App>? _factory;
    private readonly List<IBrowserContext> _contexts = [];

    public IBrowser Browser => _browser ?? throw new InvalidOperationException("Browser not initialized");

    public string ServerAddress => _factory?.ServerAddress ?? throw new InvalidOperationException("Server not started");

    public async Task InitializeAsync()
    {
        // Start the test server with Kestrel
        _factory = new WebTestingHostFactory<App>();
        // Force server to start by accessing ServerAddress which calls EnsureServer
        var address = _factory.ServerAddress;

        await WaitUntilServerRespondsAsync(address);
        Console.WriteLine($"Test server started at: {address}");

        // Initialize Playwright and launch browser
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });
    }

    /// <summary>
    /// Polls the server until it answers, before any test starts waiting on rendered UI.
    /// Accessing <c>ServerAddress</c> starts the host but does not wait for the pipeline to serve
    /// requests, so without this the whole startup budget was the first assertion's element
    /// timeout. When that ran out on a loaded agent every test in the collection failed at once
    /// with "element not found", which reads as a broken application rather than a slow start.
    /// </summary>
    private static async Task WaitUntilServerRespondsAsync(string address)
    {
        using var client = new HttpClient { Timeout = ProbeTimeout };

        var deadline = DateTime.UtcNow + ServerStartTimeout;
        Exception? lastFailure = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(address);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                lastFailure = new HttpRequestException($"Server answered {(int)response.StatusCode}.");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastFailure = exception;
            }

            await Task.Delay(ProbeInterval);
        }

        throw new InvalidOperationException(
            $"The test server at {address} did not answer within {ServerStartTimeout.TotalSeconds:0} seconds. " +
            "Every test in this collection would otherwise fail on a missing element, which hides the real cause.",
            lastFailure);
    }

    public async Task DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.CloseAsync();
        }
        _contexts.Clear();

        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>
    /// Creates a new browser context and page for isolated test execution.
    /// The context is tracked and disposed when the fixture is torn down.
    /// </summary>
    public async Task<IPage> CreatePageAsync()
    {
        var context = await Browser.NewContextAsync();
        _contexts.Add(context);
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
