using HomeBlaze.Authorization.Data;
using HomeBlaze.Authorization.Roles;
using HomeBlaze.Authorization.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

/// <summary>
/// Tests for IdentitySeeding hosted service.
/// </summary>
public class IdentitySeedingTests : IAsyncLifetime
{
    private ServiceProvider _serviceProvider = null!;
    private IdentitySeeding _seeding = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();

        // Add in-memory database
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AuthorizationDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        // Add Identity
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AuthorizationDbContext>();

        // Add RoleExpander (required by IdentitySeeding)
        services.AddSingleton<IRoleExpander, RoleExpander>();

        _serviceProvider = services.BuildServiceProvider();

        // Create database schema
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        _seeding = new IdentitySeeding(_serviceProvider, NullLogger<IdentitySeeding>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (_serviceProvider != null)
        {
            await _serviceProvider.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_CreatesDefaultRoles()
    {
        // Act
        await _seeding.StartAsync(CancellationToken.None);

        // Assert
        using var scope = _serviceProvider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        Assert.True(await roleManager.RoleExistsAsync(DefaultRoles.Anonymous));
        Assert.True(await roleManager.RoleExistsAsync(DefaultRoles.Guest));
        Assert.True(await roleManager.RoleExistsAsync(DefaultRoles.User));
        Assert.True(await roleManager.RoleExistsAsync(DefaultRoles.Operator));
        Assert.True(await roleManager.RoleExistsAsync(DefaultRoles.Supervisor));
        Assert.True(await roleManager.RoleExistsAsync(DefaultRoles.Admin));
    }

    [Fact]
    public async Task StartAsync_CreatesRoleCompositions()
    {
        // Act
        await _seeding.StartAsync(CancellationToken.None);

        // Assert
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();

        var compositions = await dbContext.RoleCompositions.ToListAsync();

        // Verify hierarchy: Guest includes Anonymous
        Assert.Contains(compositions, c => c.RoleName == DefaultRoles.Guest && c.IncludesRole == DefaultRoles.Anonymous);

        // Verify hierarchy: User includes Guest
        Assert.Contains(compositions, c => c.RoleName == DefaultRoles.User && c.IncludesRole == DefaultRoles.Guest);

        // Verify hierarchy: Operator includes User
        Assert.Contains(compositions, c => c.RoleName == DefaultRoles.Operator && c.IncludesRole == DefaultRoles.User);

        // Verify hierarchy: Supervisor includes Operator
        Assert.Contains(compositions, c => c.RoleName == DefaultRoles.Supervisor && c.IncludesRole == DefaultRoles.Operator);

        // Verify hierarchy: Admin includes Supervisor
        Assert.Contains(compositions, c => c.RoleName == DefaultRoles.Admin && c.IncludesRole == DefaultRoles.Supervisor);
    }

    [Fact]
    public async Task StartAsync_CreatesAdminUser_WhenNoUsersExist()
    {
        // Act
        await _seeding.StartAsync(CancellationToken.None);

        // Assert
        using var scope = _serviceProvider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var adminUser = await userManager.FindByNameAsync("admin");
        Assert.NotNull(adminUser);
        Assert.Equal("admin@localhost", adminUser.Email);
        Assert.True(adminUser.MustChangePassword);
        Assert.True(adminUser.EmailConfirmed);
    }

    [Fact]
    public async Task StartAsync_AdminUserHasAdminRole()
    {
        // Act
        await _seeding.StartAsync(CancellationToken.None);

        // Assert
        using var scope = _serviceProvider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var adminUser = await userManager.FindByNameAsync("admin");
        Assert.NotNull(adminUser);

        var isInRole = await userManager.IsInRoleAsync(adminUser, DefaultRoles.Admin);
        Assert.True(isInRole);
    }

    [Fact]
    public async Task StartAsync_DoesNotCreateAdminUser_WhenUsersExist()
    {
        // Arrange - create an existing user first
        using (var scope = _serviceProvider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

            // Create the User role first (required to add user to role)
            await roleManager.CreateAsync(new IdentityRole(DefaultRoles.User));

            var existingUser = new ApplicationUser
            {
                UserName = "existinguser",
                Email = "existing@test.com",
                EmailConfirmed = true
            };
            await userManager.CreateAsync(existingUser);
        }

        // Act
        await _seeding.StartAsync(CancellationToken.None);

        // Assert - admin user should not be created
        using (var scope = _serviceProvider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var adminUser = await userManager.FindByNameAsync("admin");
            Assert.Null(adminUser);
        }
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        // Act - run seeding twice
        await _seeding.StartAsync(CancellationToken.None);
        await _seeding.StartAsync(CancellationToken.None);

        // Assert - should have exactly 6 roles, not 12
        using var scope = _serviceProvider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        var roleCount = await roleManager.Roles.CountAsync();
        Assert.Equal(6, roleCount);
    }

    [Fact]
    public async Task StartAsync_RoleCompositionsAreIdempotent()
    {
        // Act - run seeding twice
        await _seeding.StartAsync(CancellationToken.None);
        await _seeding.StartAsync(CancellationToken.None);

        // Assert - should have exactly 5 compositions (one per hierarchy link)
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();

        var compositionCount = await dbContext.RoleCompositions.CountAsync();
        Assert.Equal(5, compositionCount);
    }

    [Fact]
    public async Task StopAsync_CompletesImmediately()
    {
        // Act & Assert - should complete without error
        await _seeding.StopAsync(CancellationToken.None);
    }
}
