using System.Text;
using HomeBlaze.Authentication;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using HomeBlaze.Components;
using HomeBlaze.E2E.Tests.Models;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Files;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;

// Every fixture starts a server over the same test data, so no two collections may run at once.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// xUnit collection fixture that manages Playwright browser and the test server.
/// Shared across all tests in the same collection for efficiency.
/// </summary>
public class PlaywrightFixture : IAsyncLifetime
{
    /// <summary>The password of the administrator, once the fixture claimed it, and of every person of <see cref="AddPersonAsync"/>.</summary>
    public const string Password = "e2e-test-password";

    private const float SignInTimeout = 30000;

    // The default timeout of AsyncTestHelpers.WaitUntilAsync, which the polls of the files use.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private readonly string _authenticationDirectory = Path.Combine(Path.GetTempPath(), "HomeBlaze.E2E", Guid.NewGuid().ToString("N"));

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private WebTestingHostFactory<App>? _factory;
    private IBrowserContext? _context;

    // The cookies of the signed-in administrator, which every page signed in as the administrator starts with.
    private string? _adminStorageState;

    // Counts the browser contexts of every fixture, each of which gets a client address of its own.
    private static int _clientCount;

    // Where the server log stood when the current page was created, see WaitForUrlAsync.
    private int _serverLogCountAtPage;

    public IBrowser Browser => _browser ?? throw new InvalidOperationException("Browser not initialized");

    public string ServerAddress => _factory?.ServerAddress ?? throw new InvalidOperationException("Server not started");

    /// <summary>
    /// The services of the server under test, for tests that change the object graph while a page is open.
    /// </summary>
    public IServiceProvider ServerServices => _factory?.ServerServices ?? throw new InvalidOperationException("Server not started");

    /// <summary>
    /// The warnings and errors that the server under test logs.
    /// </summary>
    public ServerLogRecorder ServerLog => _factory?.ServerLog ?? throw new InvalidOperationException("Server not started");

    /// <summary>
    /// Whether the fixture sets <see cref="Password"/> for the seeded administrator and signs them in, so that the pages of
    /// <see cref="CreatePageAsync(BrowserNewContextOptions?)"/> are signed in as a full administrator. When false, the
    /// administrator keeps the empty password of seeding and those pages are not signed in.
    /// </summary>
    protected virtual bool ClaimsAdministrator => true;

    public async Task InitializeAsync()
    {
        // Start the test server with Kestrel. It learns the test subjects and the widget before it loads anything.
        _factory = new WebTestingHostFactory<App>(
            [typeof(ControlsTestSubject), typeof(ControlsTestWidget), typeof(HistoryValuesSubject), typeof(LiveValuesSubject), typeof(LiveGridSubject), typeof(LiveGridWidget)],
            Path.Combine(_authenticationDirectory, "authentication.db"));
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

        // The startup seeds the administrator and loads the users in the background, after the root is loaded.
        var database = ServerServices.GetRequiredService<AuthenticationDatabase>();
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => database.IsReady, TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Authentication was not ready before the tests started: {database.UnavailableReason}", exception);
        }

        if (ClaimsAdministrator)
        {
            await SetPasswordAsync(AdminSeeding.AdminUserName);
            var page = await CreatePageAsync(SignedInAs.None);
            await SignInAsync(page, AdminSeeding.AdminUserName, Password);
            _adminStorageState = await page.Context.StorageStateAsync();
        }
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

        try
        {
            Directory.Delete(_authenticationDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Below the temporary folder, which the system cleans up.
        }
    }

    /// <summary>
    /// Adds a person holding <paramref name="roles"/> as the seeded administrator does in the Users tab, and gives them
    /// <see cref="Password"/>, so that <see cref="SignedInAs.User"/> signs them in at once.
    /// </summary>
    public async Task AddPersonAsync(string userName, params string[] roles)
    {
        var result = await CreateAdministration().AddUserAsync(userName, null, roles, CancellationToken.None);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"The person {userName} could not be added: {result.Error}");
        }

        await SetPasswordAsync(userName);
    }

    /// <summary>Removes a person of <see cref="AddPersonAsync"/> as the seeded administrator.</summary>
    public async Task RemovePersonAsync(string userName)
    {
        var result = await CreateAdministration().RemoveAsync(userName, CancellationToken.None);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"The person {userName} could not be removed: {result.Error}");
        }
    }

    /// <summary>The user administration acting as the seeded administrator, a full administrator.</summary>
    public IUserAdministration CreateAdministration()
    {
        var userStore = ServerServices.GetRequiredService<UserStore>();
        var adminId = userStore.GetUserId(AdminSeeding.AdminUserName)
            ?? throw new InvalidOperationException("The seeded administrator does not exist");
        return userStore.CreateAdministration(adminId);
    }

    /// <summary>
    /// Signs in through the sign-in dialog of the app at <paramref name="path"/>, the one the app shows to a visitor it stops
    /// or the one of the user menu, and waits until the page has loaded anew signed in: with the user menu, or, for a
    /// person who must set a password, with the dialog that sets it.
    /// </summary>
    public async Task SignInAsync(IPage page, string userName, string password, string path = "")
    {
        await page.GotoAsync($"{ServerAddress}{path}");
        await FillSignInDialogAsync(page, userName, password);
        await Assertions.Expect(page.GetByTestId("user-menu").Or(page.GetByTestId("set-password-dialog")))
            .ToBeVisibleAsync(new() { Timeout = SignInTimeout });
    }

    /// <summary>
    /// Opens the sign-in dialog of the current page, from the user menu when the app shows, and submits
    /// <paramref name="userName"/> and <paramref name="password"/>.
    /// </summary>
    public static async Task FillSignInDialogAsync(IPage page, string userName, string password)
    {
        var userNameInput = page.Locator("#sign-in-user-name");
        var menuSignIn = page.GetByTestId("user-menu-sign-in");
        await Assertions.Expect(userNameInput.Or(menuSignIn)).ToBeVisibleAsync(new() { Timeout = SignInTimeout });
        if (!await userNameInput.IsVisibleAsync())
        {
            await page.WaitUntilInteractiveAsync(SignInTimeout);
            await menuSignIn.ClickAsync();
        }

        await userNameInput.FillAsync(userName);
        await page.Locator("#sign-in-password").FillAsync(password);
        // Enabled once the script of the form is attached, which posts it to the endpoint that sets the cookie.
        await page.GetByTestId("sign-in-form").GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
    }

    /// <summary>
    /// Resets the seeded administrator to the empty password of seeding, as <c>--reset-admin</c> does, which ends every
    /// session, and reloads the users.
    /// </summary>
    public async Task RestoreUnclaimedAdministratorAsync()
    {
        await AdminSeeding.ResetAdminAsync(
            ServerServices.GetRequiredService<IServiceScopeFactory>(), ServerServices.GetRequiredService<AuthenticationDatabase>(), CancellationToken.None);
        await ServerServices.GetRequiredService<UserStore>().LoadAsync(CancellationToken.None);
    }

    // Through Identity, as the password endpoint stores a password, so that the person signs in without setting one first.
    private async Task SetPasswordAsync(string userName)
    {
        await using var scope = ServerServices.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByNameAsync(userName)
            ?? throw new InvalidOperationException($"The user {userName} does not exist");

        ThrowIfFailed(await userManager.AddPasswordAsync(user, Password), userName);
        user.MustChangePassword = false;
        user.AllowsEmptyPassword = false;
        ThrowIfFailed(await userManager.UpdateSecurityStampAsync(user), userName);
        await ServerServices.GetRequiredService<UserStore>().RefreshUserAsync(user.Id, CancellationToken.None);
    }

    private static void ThrowIfFailed(IdentityResult result, string userName)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"The password of {userName} could not be set: {string.Join(" ", result.Errors.Select(error => error.Description))}");
        }
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
        var subject = CreatePlainSubject(label);
        await AttachSubjectToRootAsync(name, subject);
        return (name, subject);
    }

    /// <summary>
    /// Creates a name that no subject under the root has, for a test that opens a page before the subject exists.
    /// </summary>
    public static string CreatePlainSubjectName() => $"Plain{Guid.NewGuid():N}";

    /// <summary>
    /// Creates a subject through dependency injection and puts it under the root with a new name.
    /// </summary>
    public async Task<(string Name, T Subject)> AddSubjectToRootAsync<T>() where T : IInterceptorSubject
    {
        var name = $"{typeof(T).Name}{Guid.NewGuid():N}";
        var subject = ServerServices.GetRequiredService<SubjectFactory>().CreateSubject<T>();
        await AttachSubjectToRootAsync(name, subject);
        return (name, subject);
    }

    /// <summary>
    /// Puts a subject under the root with the given name, after reconciling the storage so that an earlier test's
    /// change left on disk does not drop it again later.
    /// </summary>
    private async Task AttachSubjectToRootAsync(string name, IInterceptorSubject subject)
    {
        await ReconcileStorageAsync();

        var root = GetRoot();
        root.Children = new Dictionary<string, IInterceptorSubject>(root.Children) { [name] = subject };
    }

    /// <summary>
    /// Runs a pass of the storage now. Call it before a test sets the children of a folder or of the root itself.
    /// </summary>
    public async Task ReconcileStorageAsync()
    {
        // A pass of the storage that finds a change sets the children of a folder anew from its files, which drops what
        // a test put there. A pass now takes in what earlier tests left changed on disk, so that the pass they asked for,
        // which runs seconds later, finds nothing.
        await GetRoot().ReconcileAsync();
    }

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
    /// Takes a subject of <see cref="AddSubjectToRootAsync{T}"/> or <see cref="AddPlainSubjectToRootAsync"/> out of the
    /// graph again.
    /// </summary>
    public void RemoveSubjectFromRoot(string name, IInterceptorSubject subject)
    {
        var root = GetRoot();
        var children = new Dictionary<string, IInterceptorSubject>(root.Children);
        children.Remove(name);
        root.Children = children;
        ServerServices.GetRequiredService<SubjectFactory>().DiscardSubject(subject);
    }

    /// <summary>
    /// Stores a new motor as a JSON file of the root storage, as the create dialog does.
    /// </summary>
    /// <returns>The name of the motor under the root, which is its path, the path of its file and the motor.</returns>
    public async Task<(string Name, string FilePath, IInterceptorSubject Subject)> AddStoredMotorAsync()
    {
        var name = $"Motor{Guid.NewGuid():N}";
        var motor = ServerServices.GetRequiredService<SubjectFactory>().CreateSubject<HomeBlaze.Samples.Motor>();
        await GetRoot().AddSubjectAsync($"{name}.json", motor, CancellationToken.None);
        return (name, GetPagePath($"{name}.json"), motor);
    }

    /// <summary>
    /// Deletes a subject of <see cref="AddStoredMotorAsync"/> with its file, unless it is gone already.
    /// </summary>
    public async Task DeleteStoredSubjectAsync(string filePath, IInterceptorSubject subject)
    {
        if (File.Exists(filePath))
        {
            await GetRoot().DeleteSubjectAsync(subject, CancellationToken.None);
        }
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
    /// Reads a file of the storage of the server between the writes of the server, so that the read cannot fail one.
    /// </summary>
    public async Task<string> ReadFileAsync(string path)
    {
        var content = string.Empty;
        await RetryWhileFileIsHeldAsync(
            () => content = ReadBetweenWritesOfServer(path),
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

    // On the worker of the storage, which runs every write of the server. The server creates a file without sharing,
    // and on Unix as well that fails while the file is open for reading, so a read beside a write would fail the write.
    private string ReadBetweenWritesOfServer(string path)
    {
        var connection = GetRoot().Connection;
        var text = string.Empty;

        // Limited like the polls, so that a worker that hangs fails the test instead of holding it. Run on the thread
        // pool for the reason given in WriteDemoPageAsync.
        using var cancellation = new CancellationTokenSource(ReadTimeout);
        Task.Run(() => FluentStorageContainer.RunOperationAsync(
            connection,
            _ =>
            {
                text = File.ReadAllText(path);
                return Task.CompletedTask;
            },
            cancellation.Token)).GetAwaiter().GetResult();

        return text;
    }

    // A write that the storage gave up on can still hold the file, and the poll tries a failed read again.
    private bool TryReadFile(string path, out string text)
    {
        try
        {
            text = ReadBetweenWritesOfServer(path);
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
    /// Creates a new browser context and page for isolated test execution, signed in as the administrator when the fixture
    /// claimed them (see <see cref="ClaimsAdministrator"/>), otherwise not signed in.
    /// Closes the previous test's context, so only one is ever open.
    /// </summary>
    /// <param name="contextOptions">The options of the browser context, such as a touch device, or null for the defaults.</param>
    public Task<IPage> CreatePageAsync(BrowserNewContextOptions? contextOptions = null)
        => CreatePageAsync(_adminStorageState is null ? SignedInAs.None : SignedInAs.Admin, contextOptions);

    /// <summary>
    /// Creates a new browser context and page signed in as <paramref name="user"/>: the administrator with the cookies of
    /// the fixture, a person through the sign-in dialog with <see cref="Password"/>, which leaves the page on the start page
    /// of the app. Closes the previous test's context, so only one is ever open.
    /// </summary>
    /// <param name="user">Who the page is signed in as.</param>
    /// <param name="contextOptions">The options of the browser context, such as a touch device, or null for the defaults.</param>
    public async Task<IPage> CreatePageAsync(SignedInAs user, BrowserNewContextOptions? contextOptions = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        // An open context keeps its page's Blazor circuit alive, and the host renders every connected
        // circuit on each state change. Holding one per test for the whole run left the last tests
        // contending with two dozen idle circuits, which was enough on a two core runner for a freshly
        // loaded page to miss the clicks sent to it. Tests here run one at a time and take one page
        // each, so the previous context is finished with by the time the next one is asked for.
        if (_context is not null)
        {
            await _context.CloseAsync();
        }

        var options = contextOptions is null ? new BrowserNewContextOptions() : new BrowserNewContextOptions(contextOptions);
        options.ExtraHTTPHeaders = new Dictionary<string, string>(options.ExtraHTTPHeaders ?? new Dictionary<string, string>())
        {
            ["X-Forwarded-For"] = CreateClientAddress()
        };
        if (ReferenceEquals(user, SignedInAs.Admin))
        {
            options.StorageState = _adminStorageState
                ?? throw new InvalidOperationException("This fixture did not sign in the administrator");
        }

        _serverLogCountAtPage = ServerLog.Count;
        _context = await Browser.NewContextAsync(options);
        var page = await _context.NewPageAsync();
        if (user.UserName is { } userName && !ReferenceEquals(user, SignedInAs.Admin))
        {
            await SignInAsync(page, userName, Password);
        }

        return page;
    }

    // A network of its own per browser context, as the rate limits count an IPv6 /64 network as one client.
    private static string CreateClientAddress() => $"2001:db8:{Interlocked.Increment(ref _clientCount):x}::1";

    /// <summary>
    /// Waits until the app has navigated the page to a URL that <paramref name="isUrl"/> accepts. A timeout fails with what the
    /// page and the server show, so that a failure on a build server tells why the app did not navigate.
    /// </summary>
    public async Task WaitForUrlAsync(IPage page, Func<string, bool> isUrl, float timeout = 30000)
    {
        try
        {
            // Polls the URL of the page, which follows every navigation. WaitForURLAsync misses a navigation of the app that
            // lands between its check of the URL and its subscription to the navigation events.
            await AsyncTestHelpers.WaitUntilAsync(() => isUrl(page.Url), TimeSpan.FromMilliseconds(timeout));
        }
        catch (TimeoutException exception)
        {
            var dialogs = string.Join(" | ", await page.Locator(".mud-dialog").AllInnerTextsAsync());
            var showsErrorUi = await page.Locator("#blazor-error-ui").IsVisibleAsync();
            var serverLog = string.Join('\n', ServerLog.GetEntriesSince(_serverLogCountAtPage)
                .Select(entry => $"{entry.Level} {entry.Category}: {entry.Message} {entry.Exception}"));
            throw new TimeoutException(
                $"The page stayed at {page.Url}. Dialogs: [{dialogs}]. Error UI shown: {showsErrorUi}. Server log since the page was created:\n{serverLog}",
                exception);
        }
    }
}

/// <summary>
/// Collection definition for tests that share the PlaywrightFixture.
/// </summary>
[CollectionDefinition(nameof(PlaywrightCollection))]
public class PlaywrightCollection : ICollectionFixture<PlaywrightFixture>
{
}

/// <summary>
/// A fixture with a server of its own whose seeded administrator keeps the empty password of seeding, and whose pages
/// are not signed in by default.
/// </summary>
public sealed class UnclaimedAdministratorFixture : PlaywrightFixture
{
    protected override bool ClaimsAdministrator => false;
}

/// <summary>
/// Collection definition for tests that need the seeded administrator as seeding leaves them.
/// </summary>
[CollectionDefinition(nameof(UnclaimedAdministratorCollection))]
public class UnclaimedAdministratorCollection : ICollectionFixture<UnclaimedAdministratorFixture>
{
}
