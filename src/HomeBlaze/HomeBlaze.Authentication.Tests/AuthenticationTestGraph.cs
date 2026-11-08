using HomeBlaze.AI.Mcp;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor;
using Xunit;
using AuthorizationSubject = HomeBlaze.Authorization.Authorization;

namespace HomeBlaze.Authentication.Tests;

/// <summary>
/// A migrated <see cref="TestDatabase"/> seeded by <see cref="AdminSeeding"/> with the built-in roles and users and an
/// <c>admin</c> with <see cref="BuiltInRoles.EverythingName"/>, with a graph holding an <see cref="AuthorizationSubject"/>, the
/// <see cref="AuthorizationService"/> over it and a loaded <see cref="RoleStore"/> and <see cref="UserStore"/> projecting
/// into it.
/// </summary>
internal sealed class AuthenticationTestGraph : IAsyncDisposable
{
    private readonly TestDatabase _database;
    private readonly TestRoleNameReferences _references;
    private readonly ServiceProvider _services;
    private readonly List<AsyncServiceScope> _scopes = [];

    private AuthenticationTestGraph(
        TestDatabase database,
        TestRoleNameReferences references,
        ServiceProvider services,
        TestRoot root,
        AuthorizationService authorizationService,
        RoleStore roleStore,
        UserStore userStore)
    {
        _database = database;
        _references = references;
        _services = services;
        Root = root;
        AuthorizationService = authorizationService;
        RoleStore = roleStore;
        UserStore = userStore;
    }

    public TestRoot Root { get; }

    public AuthorizationSubject Authorization => (AuthorizationSubject)Root.Children["Authorization"];

    public AuthorizationService AuthorizationService { get; }

    public RoleStore RoleStore { get; }

    public UserStore UserStore { get; }

    /// <summary>The authentication database, initialized.</summary>
    public required AuthenticationDatabase Database { get; init; }

    /// <summary>The path of the database file.</summary>
    public string DatabasePath => _database.Path;

    /// <summary>The time of the user store and of setup codes, starting now.</summary>
    public required FakeTimeProvider Time { get; init; }

    /// <summary>
    /// A user manager of a new scope, disposed with the graph, so that it never returns a user loaded before another
    /// scope changed it.
    /// </summary>
    public UserManager<ApplicationUser> UserManager
    {
        get
        {
            var scope = _services.CreateAsyncScope();
            _scopes.Add(scope);
            return scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        }
    }

    /// <summary>A role manager of a new scope, disposed with the graph.</summary>
    public RoleManager<ApplicationRole> RoleManager
    {
        get
        {
            var scope = _services.CreateAsyncScope();
            _scopes.Add(scope);
            return scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        }
    }

    private AuthorizationProjection Projection { get; init; } = null!;

    /// <summary>The id of the seeded <c>admin</c>, a full administrator.</summary>
    public string AdminUserId => UserStore.GetUserId(AdminSeeding.AdminUserName)!;

    /// <summary>Creates a graph over a seeded database.</summary>
    public static async Task<AuthenticationTestGraph> CreateAsync()
    {
        var graph = await CreateEmptyAsync();
        Assert.Equal(SeedOutcome.Seeded, await graph.SeedAsync());
        return graph;
    }

    /// <summary>Creates a graph over an initialized database without any role or user rows.</summary>
    public static async Task<AuthenticationTestGraph> CreateEmptyAsync()
    {
        var database = new TestDatabase();
        var authenticationDatabase = new AuthenticationDatabase(database.Path, database.CreateContext, NullLogger<AuthenticationDatabase>.Instance);
        await authenticationDatabase.InitializeAsync(CancellationToken.None);

        var services = CreateServices(database);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var context = TestContext.Create();
        // The path provider of the application, an instance like the one of the MCP tools.
        var authorizationService = new AuthorizationService(context, new RulePathProvider());
        var root = new TestRoot(context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = new AuthorizationSubject() }
        };

        var projection = new AuthorizationProjection();
        projection.Attach((AuthorizationSubject)root.Children["Authorization"]);

        var references = new TestRoleNameReferences(authorizationService);
        var gate = new AuthorizationChangeGate();
        UserStore? userStore = null;
        var roleStore = new RoleStore(
            new TestDbContextFactory(database),
            authorizationService,
            [references],
            () => userStore!.GetAllRoleSets(),
            () => userStore!.GetPersonRoleSets(),
            projection,
            gate);
        userStore = new UserStore(
            services.GetRequiredService<IServiceScopeFactory>(),
            authorizationService,
            roleStore,
            projection,
            gate,
            time);
        await roleStore.LoadAsync(CancellationToken.None);
        await userStore.LoadAsync(CancellationToken.None);

        return new AuthenticationTestGraph(database, references, services, root, authorizationService, roleStore, userStore)
        {
            Database = authenticationDatabase,
            Time = time,
            Projection = projection
        };
    }

    /// <summary>Seeds the database with <see cref="AdminSeeding.SeedAsync"/> and reloads the roles and users.</summary>
    public async Task<SeedOutcome> SeedAsync()
    {
        var outcome = await AdminSeeding.SeedAsync(_services.GetRequiredService<IServiceScopeFactory>(), Database, CancellationToken.None);
        await RoleStore.LoadAsync(CancellationToken.None);
        await UserStore.LoadAsync(CancellationToken.None);
        return outcome;
    }

    /// <summary>Resets the administrator with <see cref="AdminSeeding.ResetAdminAsync"/> and reloads the roles and users.</summary>
    public async Task ResetAdminAsync()
    {
        await AdminSeeding.ResetAdminAsync(_services.GetRequiredService<IServiceScopeFactory>(), Database, CancellationToken.None);
        await RoleStore.LoadAsync(CancellationToken.None);
        await UserStore.LoadAsync(CancellationToken.None);
    }

    /// <summary>Creates a person named <paramref name="userName"/> without roles directly in the database and reloads the users.</summary>
    public async Task<ApplicationUser> CreateUserAsync(string userName)
    {
        var user = new ApplicationUser { UserName = userName };
        Assert.True((await UserManager.CreateAsync(user)).Succeeded);
        await UserStore.LoadAsync(CancellationToken.None);
        return user;
    }

    /// <summary>Sets when the lockout of the user named <paramref name="userName"/> ends, as after failed sign-ins, and reloads the users.</summary>
    public async Task SetLockoutEndAsync(string userName, DateTimeOffset? lockoutEnd)
    {
        var userManager = UserManager;
        var user = (await userManager.FindByNameAsync(userName))!;
        Assert.True((await userManager.SetLockoutEndDateAsync(user, lockoutEnd)).Succeeded);
        await UserStore.LoadAsync(CancellationToken.None);
    }

    /// <summary>Whether renaming a role in the principals fails, as when their configuration cannot be written.</summary>
    public bool FailRenames
    {
        get => _references.FailRenames;
        set => _references.FailRenames = value;
    }

    /// <summary>Creates a context of the database.</summary>
    public AuthenticationDbContext CreateDbContext() => _database.CreateContext();

    /// <summary>Adds a principal holding <paramref name="roleNames"/> that the role store can find and rename.</summary>
    public TestPrincipal AddAgentPrincipal(IReadOnlyList<string> roleNames) => _references.Add("Agent", roleNames);

    /// <summary>Adds a principal named <paramref name="name"/> holding <paramref name="roleNames"/> that the role store can find and rename.</summary>
    public TestPrincipal AddAgentPrincipal(string name, IReadOnlyList<string> roleNames) => _references.Add(name, roleNames);

    /// <summary>Adds a user holding <paramref name="roleNames"/> as the seeded <c>admin</c>.</summary>
    public async Task AddUserAsync(string userName, IReadOnlyCollection<string> roleNames)
    {
        var result = await AdministrationAs(AdminUserId).AddUserAsync(userName, null, roleNames, CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
    }

    /// <summary>Makes Identity refuse to store a user for which the predicate holds; null stores every user.</summary>
    public Func<ApplicationUser, bool>? FailUserUpdatesWhen
    {
        get => _services.GetRequiredService<TestUserValidator>().FailWhen;
        set => _services.GetRequiredService<TestUserValidator>().FailWhen = value;
    }

    /// <summary>Detaches the projection from the authorization subject, as when no such subject exists.</summary>
    public void DetachProjection() => Projection.Attach(null);

    /// <summary>Attaches the projection again to the authorization subject it is attached to, as a late notice of its attach does.</summary>
    public void AttachProjectionAgain() => Projection.Attach(Authorization);

    /// <summary>Sets the password of the user named <paramref name="userName"/>, as after the user set it with a setup code, and reloads the users.</summary>
    public async Task SetPasswordAsync(string userName, string password)
    {
        await using var scope = _services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await userManager.FindByNameAsync(userName))!;
        Assert.True((await userManager.AddPasswordAsync(user, password)).Succeeded);
        user.MustChangePassword = false;
        Assert.True((await userManager.UpdateAsync(user)).Succeeded);
        await UserStore.LoadAsync(CancellationToken.None);
    }

    /// <summary>Gets the stored user named <paramref name="userName"/> and whether <paramref name="password"/> is their password.</summary>
    public async Task<(ApplicationUser User, bool IsPassword, int TokenCount)> GetStoredUserAsync(string userName, string password)
    {
        await using var scope = _services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var context = scope.ServiceProvider.GetRequiredService<AuthenticationDbContext>();
        var user = (await userManager.FindByNameAsync(userName))!;
        return (user, await userManager.CheckPasswordAsync(user, password), await context.UserTokens.CountAsync(token => token.UserId == user.Id));
    }

    /// <summary>Gets the user administration of the user with <paramref name="userId"/>; null for nobody signed in.</summary>
    public IUserAdministration AdministrationAs(string? userId) => new UserAdministration(UserStore, () => userId);

    /// <summary>Gets the stored security stamp of the user with <paramref name="userId"/>.</summary>
    public async Task<string?> GetSecurityStampAsync(string userId)
    {
        await using var context = _database.CreateContext();
        return await context.Users.Where(user => user.Id == userId).Select(user => user.SecurityStamp).SingleAsync();
    }

    /// <summary>Links an external login to the user named <paramref name="userName"/>.</summary>
    public async Task AddExternalLoginAsync(string userName, string loginProvider, string providerKey)
    {
        await using var scope = _services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByNameAsync(userName);
        Assert.NotNull(user);
        var result = await userManager.AddLoginAsync(user, new UserLoginInfo(loginProvider, providerKey, loginProvider));
        Assert.True(result.Succeeded);
    }

    /// <summary>Counts the stored external logins of <paramref name="loginProvider"/> with <paramref name="providerKey"/>.</summary>
    public async Task<int> CountExternalLoginsAsync(string loginProvider, string providerKey)
    {
        await using var context = _database.CreateContext();
        return await context.UserLogins.CountAsync(login => login.LoginProvider == loginProvider && login.ProviderKey == providerKey);
    }

    /// <summary>Gets the names of the built-in role rows of the database.</summary>
    public async Task<List<string?>> GetBuiltInRoleNamesAsync()
    {
        await using var context = _database.CreateContext();
        return await context.Roles.Where(role => role.IsBuiltIn).Select(role => role.Name).ToListAsync();
    }

    /// <summary>Gets the names of the custom role rows of the database.</summary>
    public async Task<List<string?>> GetStoredRoleNamesAsync()
    {
        await using var context = _database.CreateContext();
        return await context.Roles.Where(role => !role.IsBuiltIn).Select(role => role.Name).ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        AuthorizationService.Dispose();
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await _services.DisposeAsync();
        _database.Dispose();
    }

    private static ServiceProvider CreateServices(TestDatabase database)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<TestUserValidator>();
        services.AddSingleton<IUserValidator<ApplicationUser>>(provider => provider.GetRequiredService<TestUserValidator>());
        services.AddDbContext<AuthenticationDbContext>(options => options.UseSqlite($"Data Source={database.Path}"));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<AuthenticationDbContext>()
            .AddDefaultTokenProviders();
        return services.BuildServiceProvider();
    }

    private sealed class TestDbContextFactory(TestDatabase database) : IDbContextFactory<AuthenticationDbContext>
    {
        public AuthenticationDbContext CreateDbContext() => database.CreateContext();
    }
}

/// <summary>A user validator that refuses the users a test selects, to make a store write fail.</summary>
internal sealed class TestUserValidator : IUserValidator<ApplicationUser>
{
    public Func<ApplicationUser, bool>? FailWhen { get; set; }

    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user) =>
        Task.FromResult(FailWhen?.Invoke(user) == true
            ? IdentityResult.Failed(new IdentityError { Code = "TestFailure", Description = "The test refuses to store this user." })
            : IdentityResult.Success);
}

/// <summary>A principal of <see cref="TestRoleNameReferences"/>.</summary>
internal sealed class TestPrincipal(string name, IReadOnlyList<string> roleNames)
{
    public string Name { get; } = name;

    public IReadOnlyList<string> Current { get; set; } = roleNames;
}

/// <summary>Principals outside the database, such as agents, that record their roles with the service as agents do.</summary>
internal sealed class TestRoleNameReferences(AuthorizationService authorizationService) : IRoleNameReferences
{
    private readonly List<TestPrincipal> _principals = [];

    public bool FailRenames { get; set; }

    public TestPrincipal Add(string name, IReadOnlyList<string> roleNames)
    {
        var principal = new TestPrincipal(name, roleNames);
        _principals.Add(principal);
        authorizationService.SetPrincipalRoles(principal, roleNames);
        return principal;
    }

    public IEnumerable<PrincipalRoleSet> GetRoleSets() => _principals.Select(principal => new PrincipalRoleSet(principal.Name, principal.Current)).ToList();

    public bool IsUsed(string roleName) =>
        _principals.Exists(principal => principal.Current.Contains(roleName, StringComparer.OrdinalIgnoreCase));

    public Task RenameAsync(string oldName, string newName, CancellationToken cancellationToken)
    {
        if (FailRenames)
        {
            throw new InvalidOperationException("The disk is full.");
        }

        foreach (var principal in _principals)
        {
            principal.Current = [.. principal.Current.Select(name => string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase) ? newName : name)];
            authorizationService.SetPrincipalRoles(principal, principal.Current);
        }

        return Task.CompletedTask;
    }
}
