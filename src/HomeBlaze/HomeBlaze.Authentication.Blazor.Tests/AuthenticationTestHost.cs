using System.Net;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;
using AuthorizationSubject = HomeBlaze.Authorization.Authorization;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>
/// A web application on a test server with the authentication of the application: a data directory in a temporary
/// folder whose root is a storage of the folder <c>Files</c>, the authentication database in it, the request pipeline,
/// the health endpoints, <c>/protected</c>, the test pages and the Blazor hub behind the gate of the app, the account
/// endpoints, and <c>/mcp</c> behind the key of the MCP user. Started once <see cref="AuthenticationStartup"/> completed.
/// </summary>
internal sealed class AuthenticationTestHost : IAsyncDisposable
{
    public const string McpPath = "/mcp";

    /// <summary>Answers the scheme, host, client address and path base of the request, separated by <c>|</c>.</summary>
    public const string EchoPath = "/echo";

    public const string HubPath = "/_blazor";

    private const string SignInPath = "/test/sign-in";

    public const string AuthorizationFileContent = """
        {
          "$type": "HomeBlaze.Authorization.Authorization"
        }
        """;

    private readonly WebApplication _application;
    private readonly DirectoryInfo _directory;

    private AuthenticationTestHost(WebApplication application, DirectoryInfo directory)
    {
        _application = application;
        _directory = directory;
    }

    public IServiceProvider Services => _application.Services;

    public AuthenticationDatabase Database => Services.GetRequiredService<AuthenticationDatabase>();

    public UserStore UserStore => Services.GetRequiredService<UserStore>();

    /// <summary>The folder of the root storage.</summary>
    public string FilesDirectory => Path.Combine(_directory.FullName, "Files");

    /// <summary>The <see cref="AuthorizationSubject"/> subjects of the graph.</summary>
    public IReadOnlyList<AuthorizationSubject> AuthorizationSubjects =>
        Services.GetRequiredService<IInterceptorSubjectContext>().GetService<ISubjectRegistry>()
            .KnownSubjects.Keys.OfType<AuthorizationSubject>().ToList();

    /// <summary>The root storage.</summary>
    public FluentStorageContainer Root => (FluentStorageContainer)Services.GetRequiredService<RootManager>().Root!;

    /// <summary>Starts the host and, unless told otherwise, waits until the authentication startup completed.</summary>
    /// <param name="anonymousRoles">The roles given to the built-in user of visitors who are not signed in.</param>
    /// <param name="databaseUnavailable">Whether the seed marker exists without users, which makes the database unavailable.</param>
    /// <param name="settings">Settings of the <c>Authentication</c> section by key, such as <c>PathBase</c>; null for none.</param>
    /// <param name="authorizationFileName">The name of a file of an <see cref="AuthorizationSubject"/> stored before the start; null for none.</param>
    /// <param name="logger">Records every log entry of the host; null for none.</param>
    /// <param name="rootType">The type of a root without settings in place of the storage of <c>Files</c>; null for the storage.</param>
    /// <param name="waitForStartup">Whether to wait until the startup completed; otherwise until the users are loaded.</param>
    public static Task<AuthenticationTestHost> StartAsync(
        IReadOnlyCollection<string>? anonymousRoles = null,
        bool databaseUnavailable = false,
        IReadOnlyDictionary<string, string>? settings = null,
        string? authorizationFileName = null,
        RecordingLogger? logger = null,
        Type? rootType = null,
        bool waitForStartup = true) =>
        StartCoreAsync(new HostSetup
        {
            AnonymousRoles = anonymousRoles,
            DatabaseUnavailable = databaseUnavailable,
            Settings = settings,
            AuthorizationFileName = authorizationFileName,
            Logger = logger,
            RootType = rootType,
            WaitForStartup = waitForStartup
        });

    /// <summary>Starts the host with <paramref name="timeProvider"/> as its clock, such as of setup codes, and waits until the authentication startup completed.</summary>
    public static Task<AuthenticationTestHost> StartAsync(TimeProvider timeProvider) =>
        StartCoreAsync(new HostSetup { TimeProvider = timeProvider });

    private static async Task<AuthenticationTestHost> StartCoreAsync(HostSetup setup)
    {
        var rootType = setup.RootType;
        var logger = setup.Logger;
        var directory = Directory.CreateTempSubdirectory("homeblaze-authentication-");
        Directory.CreateDirectory(Path.Combine(directory.FullName, "Files"));
        var rootPath = Path.Combine(directory.FullName, "Root.json");
        await File.WriteAllTextAsync(rootPath, rootType is null
            ? """
              {
                "$type": "HomeBlaze.Storage.FluentStorageContainer",
                "storageType": "disk",
                "connectionString": "Files",
                "enableFileWatching": false
              }
              """
            : $$"""{ "$type": "{{rootType.FullName}}" }""");

        if (setup.AuthorizationFileName is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "Files", setup.AuthorizationFileName), AuthorizationFileContent);
        }

        if (setup.DatabaseUnavailable)
        {
            await File.WriteAllTextAsync(AdminSeeding.MarkerPath(Path.Combine(directory.FullName, "authentication.db")), "seeded");
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        if (logger is not null)
        {
            builder.Logging.AddProvider(new RecordingLoggerProvider(logger));
        }

        builder.Configuration[HomeBlazePaths.RootConfigurationFileKey] = rootPath;
        foreach (var (key, value) in setup.Settings ?? new Dictionary<string, string>())
        {
            builder.Configuration[$"{AuthenticationOptions.SectionName}:{key}"] = value;
        }

        builder.Services.AddHomeBlazeServices();
        builder.Services.AddHomeBlazeAuthorization(new StateAttributePathProvider());
        builder.Services.AddDataProtection();
        builder.Services.AddHomeBlazeAuthentication(builder.Configuration);
        builder.Services.AddHomeBlazeWebAuthentication();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddMudServices();
        if (setup.TimeProvider is not null)
        {
            builder.Services.AddSingleton(setup.TimeProvider);
        }

        builder.Services.AddSingleton<CountingPasswordHasher>();
        builder.Services.AddSingleton<IPasswordHasher<ApplicationUser>>(services => services.GetRequiredService<CountingPasswordHasher>());
        builder.AddDefaultHealthChecks();

        var application = builder.Build();
        application.Services.GetRequiredService<TypeProvider>()
            .AddAssembly(typeof(FluentStorageContainer).Assembly)
            .AddAssembly(typeof(AuthorizationSubject).Assembly)
            .AddAssembly(typeof(AuthenticationTestHost).Assembly);

        application.UseHomeBlazeForwardedHeaders();
        application.UseHomeBlazeWebAuthentication();
        application.UseAntiforgery();
        application.UseHomeBlazeMcpClientKey(McpPath);
        application.MapDefaultEndpoints();
        application.MapGet("/protected", () => "protected");
        application.MapRazorComponents<TestApp>()
            .AddInteractiveServerRenderMode()
            .AddAdditionalAssemblies(typeof(AuthenticationWebExtensions).Assembly)
            .WithHomeBlazeAccountGate();
        application.MapHomeBlazeAccountEndpoints();
        application.MapPost(McpPath, () => "mcp").AllowAnonymous();
        application.MapGet(EchoPath, (HttpContext context) =>
            $"{context.Request.Scheme}|{context.Request.Host}|{context.Connection.RemoteIpAddress}|{context.Request.PathBase}").AllowAnonymous();
        application.MapPost(SignInPath, async (string? userName, UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager) =>
        {
            // As after setting a password, which every endpoint but the app and the password endpoint requires first.
            var user = (await userManager.FindByNameAsync(userName ?? AdminSeeding.AdminUserName))!;
            user.MustChangePassword = false;
            await userManager.UpdateAsync(user);
            await signInManager.SignInAsync(user, isPersistent: false);
        }).AllowAnonymous();

        var host = new AuthenticationTestHost(application, directory);
        try
        {
            await application.StartAsync();
            var startup = application.Services.GetServices<IHostedService>().OfType<AuthenticationStartup>().Single();
            if (setup.WaitForStartup)
            {
                await (startup.ExecuteTask ?? throw new InvalidOperationException("The authentication startup did not start."));
            }
            else
            {
                await AsyncTestHelpers.WaitUntilAsync(() => host.UserStore.GetUserId(AdminSeeding.AdminUserName) is not null);
            }

            if (setup.AnonymousRoles is not null)
            {
                var result = await host.AdministrationAsAdmin.SetRolesAsync(NameRules.AnonymousUserName, setup.AnonymousRoles, CancellationToken.None);
                Assert.True(result.Succeeded, result.Error);
            }
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        return host;
    }

    /// <summary>The user administration of the seeded <c>admin</c>, a full administrator.</summary>
    public IUserAdministration AdministrationAsAdmin =>
        UserStore.CreateAdministration(UserStore.GetUserId(AdminSeeding.AdminUserName));

    /// <summary>Creates a client of the test server, which follows no redirect.</summary>
    public HttpClient CreateClient() => _application.GetTestClient();

    /// <summary>How often a password was verified against a hash, a stored or a dummy one.</summary>
    public int PasswordVerificationCount => Services.GetRequiredService<CountingPasswordHasher>().VerificationCount;

    /// <summary>Creates a client of the test server that keeps cookies like a browser, connecting from <c>127.0.0.1</c>.</summary>
    /// <param name="isHttps">Whether it requests the pages over HTTPS instead of HTTP.</param>
    public TestBrowser CreateBrowser(bool isHttps = false) =>
        new(_application.GetTestServer().CreateHandler(context => context.Connection.RemoteIpAddress = IPAddress.Loopback),
            new Uri(isHttps ? "https://localhost/" : "http://localhost/"));

    /// <summary>
    /// Signs <paramref name="browser"/> in as the user named <paramref name="userName"/>, who no longer must change their
    /// password, without their password.
    /// </summary>
    public async Task SignInAsync(TestBrowser browser, string userName)
    {
        using var response = await browser.PostAsync($"{SignInPath}?userName={Uri.EscapeDataString(userName)}", new Dictionary<string, string>());
        response.EnsureSuccessStatusCode();
        Assert.True(browser.HasSessionCookie);
    }

    /// <summary>
    /// Gives the user named <paramref name="userName"/> <paramref name="password"/>, as after they set it, so that they
    /// sign in with it and no longer with an empty password, and reloads the users.
    /// </summary>
    public async Task SetPasswordAsync(string userName, string password)
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await userManager.FindByNameAsync(userName))!;
            Assert.True((await userManager.AddPasswordAsync(user, password)).Succeeded);
            user.AllowsEmptyPassword = false;
            user.MustChangePassword = false;
            Assert.True((await userManager.UpdateAsync(user)).Succeeded);
        }

        await UserStore.LoadAsync(CancellationToken.None);
    }

    /// <summary>Locks out the user named <paramref name="userName"/> for five minutes directly in the database, as failed sign-ins do.</summary>
    public Task LockOutAsync(string userName) => LockOutAsync(userName, DateTimeOffset.UtcNow.AddMinutes(5));

    /// <summary>
    /// Locks out the user named <paramref name="userName"/> until <paramref name="lockoutEnd"/> directly in the database,
    /// without changing their security stamp.
    /// </summary>
    public async Task LockOutAsync(string userName, DateTimeOffset lockoutEnd)
    {
        await using var scope = Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await userManager.FindByNameAsync(userName))!;
        Assert.True((await userManager.SetLockoutEndDateAsync(user, lockoutEnd)).Succeeded);
    }

    /// <summary>The user named <paramref name="userName"/> as the graph shows it.</summary>
    public User GetUser(string userName) => AuthorizationSubjects.Single().Users[userName];

    /// <summary>Sends a request built by <paramref name="configure"/> directly, which can set what a client cannot, such as its address.</summary>
    public Task<HttpContext> SendAsync(Action<HttpContext> configure) => _application.GetTestServer().SendAsync(configure);

    /// <summary>
    /// Signs in as the seeded <c>admin</c>, who no longer must change their password, and returns the session cookie as a
    /// <c>Cookie</c> header value.
    /// </summary>
    public async Task<string> SignInAsAdminAsync()
    {
        var response = await CreateClient().PostAsync(SignInPath, content: null);
        response.EnsureSuccessStatusCode();
        return string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0]));
    }

    private sealed record HostSetup
    {
        public IReadOnlyCollection<string>? AnonymousRoles { get; init; }

        public bool DatabaseUnavailable { get; init; }

        public IReadOnlyDictionary<string, string>? Settings { get; init; }

        public string? AuthorizationFileName { get; init; }

        public RecordingLogger? Logger { get; init; }

        public Type? RootType { get; init; }

        public bool WaitForStartup { get; init; } = true;

        public TimeProvider? TimeProvider { get; init; }
    }

    public async ValueTask DisposeAsync()
    {
        var connectionString = AuthenticationDatabasePool.GetConnectionString(Services);
        await _application.StopAsync();
        await _application.DisposeAsync();
        AuthenticationDatabasePool.Clear(connectionString);
        _directory.Delete(recursive: true);
    }
}
