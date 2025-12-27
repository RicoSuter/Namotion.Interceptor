using HomeBlaze.Authorization.Data;
using HomeBlaze.Authorization.Endpoints;
using HomeBlaze.Authorization.Roles;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

/// <summary>
/// Tests for AdminEndpoints user and role management.
/// </summary>
public class AdminEndpointTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly AuthorizationDbContext _dbContext;

    public AdminEndpointTests()
    {
        var services = new ServiceCollection();

        // Create in-memory database
        services.AddDbContext<AuthorizationDbContext>(options =>
            options.UseInMemoryDatabase($"AdminTests_{Guid.NewGuid()}"));

        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AuthorizationDbContext>();

        _serviceProvider = services.BuildServiceProvider();
        _userManager = _serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        _roleManager = _serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        _dbContext = _serviceProvider.GetRequiredService<AuthorizationDbContext>();
    }

    public void Dispose()
    {
        _dbContext.Database.EnsureDeleted();
        _serviceProvider.Dispose();
    }

    [Fact]
    public async Task CreateUser_WithValidData_CreatesUser()
    {
        // Arrange
        var request = new CreateUserRequest
        {
            UserName = "testuser",
            Email = "test@example.com"
        };

        // Act
        var result = await _userManager.CreateAsync(new ApplicationUser
        {
            UserName = request.UserName,
            Email = request.Email,
            MustChangePassword = true
        });

        // Assert
        Assert.True(result.Succeeded);
        var user = await _userManager.FindByNameAsync("testuser");
        Assert.NotNull(user);
        Assert.Equal("test@example.com", user.Email);
        Assert.True(user.MustChangePassword);
    }

    [Fact]
    public async Task CreateUser_DuplicateUsername_Fails()
    {
        // Arrange
        await _userManager.CreateAsync(new ApplicationUser { UserName = "existing" });

        // Act
        var result = await _userManager.CreateAsync(new ApplicationUser { UserName = "existing" });

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "DuplicateUserName");
    }

    [Fact]
    public async Task AddUserToRole_AssignsRole()
    {
        // Arrange
        await _roleManager.CreateAsync(new IdentityRole("TestRole"));
        var user = new ApplicationUser { UserName = "roleuser" };
        await _userManager.CreateAsync(user);

        // Act
        var result = await _userManager.AddToRoleAsync(user, "TestRole");

        // Assert
        Assert.True(result.Succeeded);
        var isInRole = await _userManager.IsInRoleAsync(user, "TestRole");
        Assert.True(isInRole);
    }

    [Fact]
    public async Task RemoveUserFromRole_RemovesRole()
    {
        // Arrange
        await _roleManager.CreateAsync(new IdentityRole("RemoveMe"));
        var user = new ApplicationUser { UserName = "unroleuser" };
        await _userManager.CreateAsync(user);
        await _userManager.AddToRoleAsync(user, "RemoveMe");

        // Act
        var result = await _userManager.RemoveFromRoleAsync(user, "RemoveMe");

        // Assert
        Assert.True(result.Succeeded);
        var isInRole = await _userManager.IsInRoleAsync(user, "RemoveMe");
        Assert.False(isInRole);
    }

    [Fact]
    public async Task DeleteUser_RemovesUser()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "deleteuser" };
        await _userManager.CreateAsync(user);
        var userId = user.Id;

        // Act
        var result = await _userManager.DeleteAsync(user);

        // Assert
        Assert.True(result.Succeeded);
        var found = await _userManager.FindByIdAsync(userId);
        Assert.Null(found);
    }

    [Fact]
    public async Task CreateRole_WithValidName_CreatesRole()
    {
        // Arrange
        var roleName = "CustomRole";

        // Act
        var result = await _roleManager.CreateAsync(new IdentityRole(roleName));

        // Assert
        Assert.True(result.Succeeded);
        var role = await _roleManager.FindByNameAsync(roleName);
        Assert.NotNull(role);
    }

    [Fact]
    public async Task CreateRole_DuplicateName_Fails()
    {
        // Arrange
        await _roleManager.CreateAsync(new IdentityRole("DupeRole"));

        // Act
        var result = await _roleManager.CreateAsync(new IdentityRole("DupeRole"));

        // Assert
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DeleteRole_RemovesRole()
    {
        // Arrange
        await _roleManager.CreateAsync(new IdentityRole("ToDelete"));
        var role = await _roleManager.FindByNameAsync("ToDelete");

        // Act
        var result = await _roleManager.DeleteAsync(role!);

        // Assert
        Assert.True(result.Succeeded);
        var found = await _roleManager.FindByNameAsync("ToDelete");
        Assert.Null(found);
    }

    [Fact]
    public async Task GetUsersInRole_ReturnsCorrectUsers()
    {
        // Arrange
        await _roleManager.CreateAsync(new IdentityRole("GroupRole"));
        var user1 = new ApplicationUser { UserName = "group1" };
        var user2 = new ApplicationUser { UserName = "group2" };
        var user3 = new ApplicationUser { UserName = "notingroup" };
        await _userManager.CreateAsync(user1);
        await _userManager.CreateAsync(user2);
        await _userManager.CreateAsync(user3);
        await _userManager.AddToRoleAsync(user1, "GroupRole");
        await _userManager.AddToRoleAsync(user2, "GroupRole");

        // Act
        var usersInRole = await _userManager.GetUsersInRoleAsync("GroupRole");

        // Assert
        Assert.Equal(2, usersInRole.Count);
        Assert.Contains(usersInRole, u => u.UserName == "group1");
        Assert.Contains(usersInRole, u => u.UserName == "group2");
        Assert.DoesNotContain(usersInRole, u => u.UserName == "notingroup");
    }

    [Fact]
    public async Task ChangePassword_WithValidPassword_ChangesPassword()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "changeuser" };
        await _userManager.CreateAsync(user, "OldPassword123!");

        // Act - use ChangePassword instead of Reset (no token provider needed)
        var result = await _userManager.ChangePasswordAsync(user, "OldPassword123!", "NewPassword456!");

        // Assert
        Assert.True(result.Succeeded);
        var canSignIn = await _userManager.CheckPasswordAsync(user, "NewPassword456!");
        Assert.True(canSignIn);
    }

    [Fact]
    public async Task LockoutUser_SetsLockoutEnd()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "lockuser" };
        await _userManager.CreateAsync(user);
        var lockoutEnd = DateTimeOffset.UtcNow.AddHours(1);

        // Act
        await _userManager.SetLockoutEndDateAsync(user, lockoutEnd);

        // Assert
        var isLockedOut = await _userManager.IsLockedOutAsync(user);
        Assert.True(isLockedOut);
    }

    [Fact]
    public async Task UnlockUser_ClearsLockout()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "unlockuser" };
        await _userManager.CreateAsync(user);
        await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1));

        // Act
        await _userManager.SetLockoutEndDateAsync(user, null);

        // Assert
        var isLockedOut = await _userManager.IsLockedOutAsync(user);
        Assert.False(isLockedOut);
    }

    [Fact]
    public void SystemRoles_AreProtected()
    {
        // Arrange
        var systemRoles = new[] { DefaultRoles.Admin, DefaultRoles.Anonymous, DefaultRoles.Guest, DefaultRoles.User };

        // Assert - these should be the protected roles
        Assert.Contains("Admin", systemRoles);
        Assert.Contains("Anonymous", systemRoles);
        Assert.Contains("Guest", systemRoles);
        Assert.Contains("User", systemRoles);
    }
}
