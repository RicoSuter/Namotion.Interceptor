using System.Text;
using HomeBlaze.Components;
using HomeBlaze.E2E.Tests.Models;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;
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

    /// <summary>
    /// The services of the server under test, for tests that change the object graph while a page is open.
    /// </summary>
    public IServiceProvider ServerServices => _factory?.ServerServices ?? throw new InvalidOperationException("Server not started");

    public async Task InitializeAsync()
    {
        // Start the test server with Kestrel. It learns the test subject and its widget before it loads anything.
        _factory = new WebTestingHostFactory<App>([typeof(ControlsTestSubject), typeof(ControlsTestWidget)]);
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

        // The storage of the root assigns its children in one step at the end of its scan, which runs
        // after the root is loaded. A test that reads the graph directly would otherwise race the scan.
        await AsyncTestHelpers.WaitUntilAsync(
            () => GetRoot().Children.Count > 0,
            TimeSpan.FromSeconds(60),
            message: "The storage of the root did not finish its scan before the tests started");

        // Registered once for the whole run, so that every test sees the same types. Resolving a qualified
        // name adds the type to the registry.
        var typeRegistry = _factory.ServerServices.GetRequiredService<SubjectTypeRegistry>();
        if (typeRegistry.ResolveType(typeof(PlainConfigurableSubject).AssemblyQualifiedName!) is null
            || typeRegistry.ResolveType(typeof(ControlsTestSubject).AssemblyQualifiedName!) is null)
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
    /// <param name="name">The name of the subject under the root, or null for a new one.</param>
    /// <param name="label">The label of the subject, which it has before it enters the graph.</param>
    /// <returns>The name of the subject under the root, which is its path, and the subject.</returns>
    public async Task<(string Name, PlainConfigurableSubject Subject)> AddPlainSubjectToRootAsync(string? name = null, string label = "Original label")
    {
        name ??= CreatePlainSubjectName();
        var root = GetRoot();

        // A pass of the storage that finds a change sets the children of the root anew from its files, which drops
        // this subject. A pass now takes in what earlier tests left changed on disk, so that the pass they asked
        // for, which runs seconds later, finds nothing.
        await root.ReconcileAsync();

        var subject = CreatePlainSubject(label);
        root.Children = new Dictionary<string, IInterceptorSubject>(root.Children) { [name] = subject };
        return (name, subject);
    }

    /// <summary>
    /// Creates a name that no subject under the root has, for a test that opens a page before the subject exists.
    /// </summary>
    public static string CreatePlainSubjectName() => $"Plain{Guid.NewGuid():N}";

    /// <summary>
    /// Puts a new subject in the place of a subject of <see cref="AddPlainSubjectToRootAsync"/> in one write, like a
    /// device that connects again and gets a new subject under the name it had.
    /// </summary>
    /// <returns>The subject that is under the root now.</returns>
    public PlainConfigurableSubject ReplacePlainSubjectInRoot(string name, PlainConfigurableSubject replaced, string label)
    {
        var root = GetRoot();
        var subject = CreatePlainSubject(label);
        root.Children = new Dictionary<string, IInterceptorSubject>(root.Children) { [name] = subject };
        ServerServices.GetRequiredService<SubjectFactory>().DiscardSubject(replaced);
        return subject;
    }

    /// <summary>
    /// Takes a subject of <see cref="AddPlainSubjectToRootAsync"/> out of the graph again.
    /// </summary>
    public void RemovePlainSubjectFromRoot(string name, PlainConfigurableSubject subject)
    {
        var root = GetRoot();
        var children = new Dictionary<string, IInterceptorSubject>(root.Children);
        children.Remove(name);
        root.Children = children;
        ServerServices.GetRequiredService<SubjectFactory>().DiscardSubject(subject);
    }

    /// <summary>
    /// Writes a page of the demo folder, given by its file name, through the page like an editor does, which also
    /// loads the page again.
    /// </summary>
    public async Task WriteDemoPageAsync(string name, string content)
    {
        var folder = (VirtualFolder)GetRoot().Children["Demo"];
        var file = (MarkdownFile)folder.Children[name];
        var bytes = Encoding.UTF8.GetBytes(content);

        await RetryWhileFileIsHeldAsync(
            () =>
            {
                // The condition is synchronous. The write runs on the thread pool so that waiting for it here
                // cannot block a continuation that would want the synchronization context of the test.
                Task.Run(async () =>
                {
                    using var stream = new MemoryStream(bytes);
                    await file.WriteAsync(stream, CancellationToken.None);
                }).GetAwaiter().GetResult();
            },
            $"The page {name} could not be written before the test started");
    }

    /// <summary>
    /// The path of a page of the demo folder on disk, which the server reads and writes.
    /// </summary>
    public string GetDemoPagePath(string name) => GetPagePath(Path.Combine("Demo", name));

    /// <summary>
    /// The path on disk of a page, given relative to the test data folder of the server.
    /// </summary>
    public string GetPagePath(string relativePath)
        => Path.Combine(ServerServices.GetRequiredService<RootManager>().DataDirectory, "TestData", relativePath);

    /// <summary>
    /// Reads a file that the server may hold open, such as a page that it is loading again after a write.
    /// </summary>
    public async Task<string> ReadFileAsync(string path)
    {
        var content = string.Empty;
        await RetryWhileFileIsHeldAsync(
            () => content = File.ReadAllText(path),
            $"The file {path} could not be read");
        return content;
    }

    /// <summary>
    /// Reads a page of the demo folder, given by its file name, until its text with line feeds only is the expected
    /// one, and returns that text. A write that the page refuses leaves the text as it was.
    /// </summary>
    public async Task<string> WaitForDemoPageTextAsync(string name, Func<string, bool> isExpected, string message)
    {
        var path = GetDemoPagePath(name);
        var text = string.Empty;

        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                if (!TryReadFile(path, out var written))
                {
                    return false;
                }

                text = written.Replace("\r\n", "\n");
                return isExpected(text);
            },
            message: message);

        return text;
    }

    // The server holds the file open while it reads it again after a write, and the poll tries a failed read again.
    private static bool TryReadFile(string path, out string text)
    {
        try
        {
            text = File.ReadAllText(path);
            return true;
        }
        catch (IOException)
        {
            text = string.Empty;
            return false;
        }
    }

    // The server reads a file again after every write and keeps it open while it parses the page, which can still be
    // going on for the write of the test before this one. A sharing violation fails when the file is opened and
    // reads or writes nothing, so the operation is safe to try again.
    private static async Task RetryWhileFileIsHeldAsync(Action operation, string failureMessage)
    {
        IOException? lastError = null;
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    try
                    {
                        operation();
                        return true;
                    }
                    catch (IOException exception)
                    {
                        lastError = exception;
                        return false;
                    }
                },
                TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException) when (lastError is not null)
        {
            throw new IOException(failureMessage, lastError);
        }
    }

    private PlainConfigurableSubject CreatePlainSubject(string label)
    {
        var subject = ServerServices.GetRequiredService<SubjectFactory>().CreateSubject<PlainConfigurableSubject>();
        subject.Label = label;
        return subject;
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
