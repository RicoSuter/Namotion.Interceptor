using System.Security.Claims;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Authorization;
using HomeBlaze.Services.Lifecycle;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;
using AuthorizationService = HomeBlaze.Authorization.AuthorizationService;
using AuthorizationSubject = HomeBlaze.Authorization.Authorization;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>
/// A graph of a root holding an <see cref="AuthorizationSubject"/> (<c>/Authorization</c>), a
/// <see cref="PermissionsTestStorage"/> (<c>/Storage</c>) and a <see cref="PermissionsTestDevice"/> (<c>/Device</c>), over a seeded
/// authentication database registered with <c>AddHomeBlazeAuthentication</c> and <c>AddHomeBlazeWebAuthentication</c>,
/// and the <see cref="PolicyUserPermissions"/> of a viewer: the person <see cref="TestUserName"/> signed in, or a visitor
/// who is not signed in.
/// </summary>
internal sealed class PermissionsTestGraph : IAsyncDisposable
{
    public const string TestUserName = "permissions";

    private readonly ServiceProvider _services;
    private readonly string _databasePath;
    private readonly TestAuthenticationStateProvider _authenticationState = new();
    private CurrentUser? _currentUser;

    private PermissionsTestGraph(ServiceProvider services, string databasePath, PermissionsTestRoot root)
    {
        _services = services;
        _databasePath = databasePath;
        Root = root;
    }

    public PermissionsTestRoot Root { get; }

    public AuthorizationSubject Authorization => (AuthorizationSubject)Root.Children["Authorization"];

    public PermissionsTestStorage Storage => (PermissionsTestStorage)Root.Children["Storage"];

    public PermissionsTestDevice Device => (PermissionsTestDevice)Root.Children["Device"];

    public UserStore UserStore => _services.GetRequiredService<UserStore>();

    public RoleStore RoleStore => _services.GetRequiredService<RoleStore>();

    public AuthorizationService AuthorizationService => _services.GetRequiredService<AuthorizationService>();

    /// <summary>The current user of the viewer, which <see cref="Permissions"/> follows.</summary>
    public CurrentUser CurrentUser => _currentUser!;

    /// <summary>The viewer access of the viewer.</summary>
    public PolicyUserPermissions Permissions { get; private set; } = null!;

    /// <summary>The services of the graph.</summary>
    public IServiceProvider Services => _services;

    /// <summary>The authentication state of the viewer, which <see cref="CurrentUser"/> follows.</summary>
    public AuthenticationStateProvider AuthenticationStateProvider => _authenticationState;

    /// <summary>
    /// Creates the graph with the viewer signed in holding <paramref name="roles"/>, or, when null, not signed in.
    /// </summary>
    /// <param name="roles">The roles of the person <see cref="TestUserName"/>; null for a visitor who is not signed in.</param>
    /// <param name="customRoles">The custom roles saved before the viewer is added.</param>
    /// <param name="anonymousRoles">The roles of the built-in user of visitors who are not signed in; null for none.</param>
    public static async Task<PermissionsTestGraph> CreateAsync(
        IReadOnlyCollection<string>? roles,
        IReadOnlyCollection<AuthorizationRole>? customRoles = null,
        IReadOnlyCollection<string>? anonymousRoles = null)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"homeblaze-viewer-{Guid.NewGuid():N}.db");
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(() => new MethodPropertyInitializer(), handler => handler is MethodPropertyInitializer)
            .WithService<IPropertyLifecycleHandler>(() => new PropertyAttributeInitializer(), handler => handler is PropertyAttributeInitializer);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{AuthenticationOptions.SectionName}:{nameof(AuthenticationOptions.Database)}"] = databasePath })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(context);
        services.AddHomeBlazeAuthorization(new RulePathProvider());
        services.AddHomeBlazeAuthentication(configuration);
        services.AddHomeBlazeWebAuthentication();
        var provider = services.BuildServiceProvider();

        // The service observes the graph from its creation, so it is created before the subjects.
        _ = provider.GetRequiredService<AuthorizationService>();
        var root = new PermissionsTestRoot(context)
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["Authorization"] = new AuthorizationSubject(),
                ["Storage"] = new PermissionsTestStorage(),
                ["Device"] = new PermissionsTestDevice()
            }
        };

        var graph = new PermissionsTestGraph(provider, databasePath, root);
        try
        {
            await graph.InitializeAsync(roles, customRoles ?? [], anonymousRoles);
        }
        catch
        {
            await graph.DisposeAsync();
            throw;
        }

        return graph;
    }

    /// <summary>Adds the user named <paramref name="userName"/> holding <paramref name="roles"/> as the seeded <c>admin</c> and returns them as the graph shows them.</summary>
    public async Task<User> AddUserAsync(string userName, IReadOnlyCollection<string>? roles = null)
    {
        var result = await AdministrationAsAdmin.AddUserAsync(userName, null, roles ?? [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
        return Authorization.Users[userName];
    }

    /// <summary>Replaces the roles of the viewer as the seeded <c>admin</c>.</summary>
    public async Task SetRolesAsync(IReadOnlyCollection<string> roles)
    {
        var result = await AdministrationAsAdmin.SetRolesAsync(TestUserName, roles, CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
    }

    /// <summary>Removes the viewer as the seeded <c>admin</c>, while their principal stays signed in.</summary>
    public async Task RemoveTestUserAsync()
    {
        var result = await AdministrationAsAdmin.RemoveAsync(TestUserName, CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
    }

    /// <summary>Signs the viewer out, as a circuit whose authentication state became anonymous.</summary>
    public void SignOut() => _authenticationState.SetPrincipal(new ClaimsPrincipal(new ClaimsIdentity()));

    /// <summary>
    /// Signs the person <see cref="TestUserName"/> in as the viewer, with the claim that they must change their password
    /// when <paramref name="mustChangePassword"/>.
    /// </summary>
    public void SignInTestUser(bool mustChangePassword = false) =>
        _authenticationState.SetPrincipal(CreateTestUserPrincipal(UserStore.GetUserId(TestUserName)!, mustChangePassword));

    public async ValueTask DisposeAsync()
    {
        Permissions?.Dispose();
        _currentUser?.Dispose();
        var connectionString = AuthenticationDatabasePool.GetConnectionString(_services);
        await _services.DisposeAsync();
        AuthenticationDatabasePool.Clear(connectionString);
        File.Delete(_databasePath);
        File.Delete(AdminSeeding.MarkerPath(_databasePath));
    }

    private IUserAdministration AdministrationAsAdmin => UserStore.CreateAdministration(UserStore.GetUserId(AdminSeeding.AdminUserName));

    private async Task InitializeAsync(IReadOnlyCollection<string>? roles, IReadOnlyCollection<AuthorizationRole> customRoles, IReadOnlyCollection<string>? anonymousRoles)
    {
        var database = _services.GetRequiredService<AuthenticationDatabase>();
        await database.InitializeAsync(CancellationToken.None);
        Assert.Equal(SeedOutcome.Seeded, await AdminSeeding.SeedAsync(_services.GetRequiredService<IServiceScopeFactory>(), database, CancellationToken.None));

        _services.GetRequiredService<AuthorizationProjection>().Attach(Authorization);
        var roleStore = _services.GetRequiredService<RoleStore>();
        await roleStore.LoadAsync(CancellationToken.None);
        await UserStore.LoadAsync(CancellationToken.None);

        foreach (var role in customRoles)
        {
            var saved = await roleStore.SaveRoleAsync(null, role, null, CancellationToken.None);
            Assert.True(saved.Succeeded, saved.Error);
        }

        if (anonymousRoles is not null)
        {
            var result = await AdministrationAsAdmin.SetRolesAsync(NameRules.AnonymousUserName, anonymousRoles, CancellationToken.None);
            Assert.True(result.Succeeded, result.Error);
        }

        if (roles is not null)
        {
            await AddUserAsync(TestUserName, roles);
            _authenticationState.SetPrincipal(CreateTestUserPrincipal(UserStore.GetUserId(TestUserName)!, mustChangePassword: false));
        }

        _currentUser = new CurrentUser(
            _authenticationState, UserStore, _services.GetRequiredService<IOptions<IdentityOptions>>(), _services.GetRequiredService<ILogger<CurrentUser>>());
        Permissions = new PolicyUserPermissions(AuthorizationService, _currentUser, _services.GetRequiredService<ILogger<PolicyUserPermissions>>());
    }

    private static ClaimsPrincipal CreateTestUserPrincipal(string userId, bool mustChangePassword)
    {
        List<Claim> claims = [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, TestUserName)];
        if (mustChangePassword)
        {
            claims.Add(new Claim(HomeBlazeClaimTypes.MustChangePassword, "true"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    /// <summary>The authentication state of a circuit, which a test sets; not signed in until then.</summary>
    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        private volatile Task<AuthenticationState> _state = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state;

        public void SetPrincipal(ClaimsPrincipal principal)
        {
            _state = Task.FromResult(new AuthenticationState(principal));
            NotifyAuthenticationStateChanged(_state);
        }
    }
}
