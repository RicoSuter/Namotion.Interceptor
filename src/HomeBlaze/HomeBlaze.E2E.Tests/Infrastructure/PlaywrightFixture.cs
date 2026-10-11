using HomeBlaze.Components;
using HomeBlaze.E2E.Tests.Models;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;

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

    /// <summary>
    /// The services of the server under test, for tests that change the object graph while a page is open.
    /// </summary>
    public IServiceProvider ServerServices => _factory?.ServerServices ?? throw new InvalidOperationException("Server not started");

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

        // Registered once for the whole run, so that every test sees the same types. Resolving a qualified
        // name adds the type to the registry.
        var typeRegistry = _factory.ServerServices.GetRequiredService<SubjectTypeRegistry>();
        if (typeRegistry.ResolveType(typeof(PlainConfigurableSubject).AssemblyQualifiedName!) is null)
        {
            throw new InvalidOperationException("The test-only subject type could not be registered");
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
    /// Puts a subject without an edit component under the root, for which the edit dialog and the page editor
    /// show the generated properties editor. The serializer does not know the test-only type, so it cannot be
    /// created through the create dialog, which stores it in a file.
    /// </summary>
    /// <returns>The name of the subject under the root, which is its path, and the subject.</returns>
    public (string Name, PlainConfigurableSubject Subject) AddPlainSubjectToRoot()
    {
        var name = $"Plain{Guid.NewGuid():N}";
        var root = GetRoot();
        var subject = ServerServices.GetRequiredService<SubjectFactory>().CreateSubject<PlainConfigurableSubject>();
        subject.Label = "Original label";
        root.Children = new Dictionary<string, IInterceptorSubject>(root.Children) { [name] = subject };
        return (name, subject);
    }

    /// <summary>
    /// Takes a subject of <see cref="AddPlainSubjectToRoot"/> out of the graph again.
    /// </summary>
    public void RemovePlainSubjectFromRoot(string name, PlainConfigurableSubject subject)
    {
        var root = GetRoot();
        var children = new Dictionary<string, IInterceptorSubject>(root.Children);
        children.Remove(name);
        root.Children = children;
        ServerServices.GetRequiredService<SubjectFactory>().DiscardSubject(subject);
    }

    private FluentStorageContainer GetRoot()
    {
        return ServerServices.GetRequiredService<RootManager>().Root as FluentStorageContainer
            ?? throw new InvalidOperationException("The root of the test server is not a storage container");
    }

    /// <summary>
    /// Creates a new browser context and page for isolated test execution.
    /// Closes the previous test's context, so only one is ever open.
    /// </summary>
    /// <param name="contextOptions">The options of the browser context, such as a touch device, or null for the defaults.</param>
    public async Task<IPage> CreatePageAsync(BrowserNewContextOptions? contextOptions = null)
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

        _context = await Browser.NewContextAsync(contextOptions);
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
