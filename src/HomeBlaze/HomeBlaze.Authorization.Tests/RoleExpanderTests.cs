using HomeBlaze.Authorization.Data;
using HomeBlaze.Authorization.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

/// <summary>
/// Tests for role expansion with various hierarchy configurations.
/// </summary>
public class RoleExpanderTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly AuthorizationDbContext _dbContext;

    public RoleExpanderTests()
    {
        var databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<AuthorizationDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));

        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<AuthorizationDbContext>();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

    [Fact]
    public async Task ExpandRoles_WithDefaultHierarchy_ExpandsAdminToAllRoles()
    {
        // Arrange
        SetupDefaultHierarchy();
        var expander = await CreateInitializedExpanderAsync();

        // Act
        var expanded = expander.ExpandRoles(["Admin"]);

        // Assert
        Assert.Contains("Admin", expanded);
        Assert.Contains("Supervisor", expanded);
        Assert.Contains("Operator", expanded);
        Assert.Contains("User", expanded);
        Assert.Contains("Guest", expanded);
        Assert.Contains("Anonymous", expanded);
        Assert.Equal(6, expanded.Count);
    }

    [Fact]
    public async Task ExpandRoles_GuestRole_OnlyIncludesAnonymous()
    {
        // Arrange
        SetupDefaultHierarchy();
        var expander = await CreateInitializedExpanderAsync();

        // Act
        var expanded = expander.ExpandRoles(["Guest"]);

        // Assert
        Assert.Contains("Guest", expanded);
        Assert.Contains("Anonymous", expanded);
        Assert.Equal(2, expanded.Count);
    }

    [Fact]
    public async Task ExpandRoles_UnknownRole_ReturnsOnlyItself()
    {
        // Arrange - no hierarchy setup, but still need to initialize
        var expander = await CreateInitializedExpanderAsync();

        // Act
        var expanded = expander.ExpandRoles(["CustomRole"]);

        // Assert
        Assert.Single(expanded);
        Assert.Contains("CustomRole", expanded);
    }

    [Fact]
    public async Task ExpandRoles_EmptyInput_ReturnsEmptySet()
    {
        // Arrange
        var expander = await CreateInitializedExpanderAsync();

        // Act
        var expanded = expander.ExpandRoles([]);

        // Assert
        Assert.Empty(expanded);
    }

    [Fact]
    public async Task ExpandRoles_MultipleRoles_CombinesHierarchies()
    {
        // Arrange
        _dbContext.RoleCompositions.AddRange(
            new RoleComposition { RoleName = "Editor", IncludesRole = "Viewer" },
            new RoleComposition { RoleName = "Reviewer", IncludesRole = "Viewer" }
        );
        _dbContext.SaveChanges();
        var expander = await CreateInitializedExpanderAsync();

        // Act
        var expanded = expander.ExpandRoles(["Editor", "Reviewer"]);

        // Assert
        Assert.Contains("Editor", expanded);
        Assert.Contains("Reviewer", expanded);
        Assert.Contains("Viewer", expanded);
        Assert.Equal(3, expanded.Count); // No duplicates
    }

    [Fact]
    public async Task ExpandRoles_DiamondInheritance_NoDuplicates()
    {
        // Arrange: Admin -> [User, SecurityGuard], both -> Guest
        _dbContext.RoleCompositions.AddRange(
            new RoleComposition { RoleName = "Admin", IncludesRole = "User" },
            new RoleComposition { RoleName = "Admin", IncludesRole = "SecurityGuard" },
            new RoleComposition { RoleName = "User", IncludesRole = "Guest" },
            new RoleComposition { RoleName = "SecurityGuard", IncludesRole = "Guest" }
        );
        _dbContext.SaveChanges();
        var expander = await CreateInitializedExpanderAsync();

        // Act
        var expanded = expander.ExpandRoles(["Admin"]);

        // Assert
        Assert.Contains("Admin", expanded);
        Assert.Contains("User", expanded);
        Assert.Contains("SecurityGuard", expanded);
        Assert.Contains("Guest", expanded);
        Assert.Equal(4, expanded.Count); // Guest only once
    }

    [Fact]
    public async Task ExpandRoles_SelfReference_IsIgnored()
    {
        // Arrange: Admin includes itself (should not cause infinite loop)
        _dbContext.RoleCompositions.AddRange(
            new RoleComposition { RoleName = "Admin", IncludesRole = "Admin" },
            new RoleComposition { RoleName = "Admin", IncludesRole = "User" }
        );
        _dbContext.SaveChanges();
        var expander = await CreateInitializedExpanderAsync();

        // Act - should not throw
        var expanded = expander.ExpandRoles(["Admin"]);

        // Assert
        Assert.Contains("Admin", expanded);
        Assert.Contains("User", expanded);
    }

    [Fact]
    public async Task ExpandRoles_CircularReference_HandledGracefully()
    {
        // Arrange: A -> B -> C -> A (circular)
        _dbContext.RoleCompositions.AddRange(
            new RoleComposition { RoleName = "A", IncludesRole = "B" },
            new RoleComposition { RoleName = "B", IncludesRole = "C" },
            new RoleComposition { RoleName = "C", IncludesRole = "A" }
        );
        _dbContext.SaveChanges();
        var expander = await CreateInitializedExpanderAsync();

        // Act - should handle cycle gracefully by stopping expansion
        var expanded = expander.ExpandRoles(["A"]);

        // Assert - all roles in the cycle are included
        Assert.Contains("A", expanded);
        Assert.Contains("B", expanded);
        Assert.Contains("C", expanded);
        Assert.Equal(3, expanded.Count);
    }

    [Fact]
    public async Task ExpandRoles_DeepHierarchy_ExpandsAllLevels()
    {
        // Arrange: 10 levels deep
        for (int i = 1; i <= 10; i++)
        {
            _dbContext.RoleCompositions.Add(new RoleComposition
            {
                RoleName = $"Level{i}",
                IncludesRole = i < 10 ? $"Level{i + 1}" : "Base"
            });
        }
        _dbContext.SaveChanges();
        var expander = await CreateInitializedExpanderAsync();

        // Act
        var expanded = expander.ExpandRoles(["Level1"]);

        // Assert - should have all 10 levels plus Base
        Assert.Equal(11, expanded.Count);
        Assert.Contains("Level1", expanded);
        Assert.Contains("Level10", expanded);
        Assert.Contains("Base", expanded);
    }

    [Fact]
    public async Task ReloadAsync_RefreshesCache()
    {
        // Arrange
        _dbContext.RoleCompositions.Add(new RoleComposition
        {
            RoleName = "Admin",
            IncludesRole = "User"
        });
        _dbContext.SaveChanges();
        var expander = await CreateInitializedExpanderAsync();

        // First expansion
        var expanded1 = expander.ExpandRoles(["Admin"]);
        Assert.Contains("User", expanded1);
        Assert.DoesNotContain("Guest", expanded1);

        // Add new composition
        _dbContext.RoleCompositions.Add(new RoleComposition
        {
            RoleName = "Admin",
            IncludesRole = "Guest"
        });
        _dbContext.SaveChanges();

        // Act
        await expander.ReloadAsync();
        var expanded2 = expander.ExpandRoles(["Admin"]);

        // Assert - new composition should be included
        Assert.Contains("User", expanded2);
        Assert.Contains("Guest", expanded2);
    }

    [Fact]
    public void ExpandRoles_WithoutInitialization_ReturnsRolesAsIs()
    {
        // Arrange - expander not initialized
        var expander = new RoleExpander(_serviceProvider);

        // Act - should gracefully handle by returning roles as-is
        var expanded = expander.ExpandRoles(["Admin", "User"]);

        // Assert - returns input roles without expansion
        Assert.Contains("Admin", expanded);
        Assert.Contains("User", expanded);
        Assert.Equal(2, expanded.Count);
    }

    [Fact]
    public async Task IsInitialized_AfterInitialize_ReturnsTrue()
    {
        // Arrange
        var expander = new RoleExpander(_serviceProvider);
        Assert.False(expander.IsInitialized);

        // Act
        await expander.InitializeAsync();

        // Assert
        Assert.True(expander.IsInitialized);
    }

    private async Task<RoleExpander> CreateInitializedExpanderAsync()
    {
        var expander = new RoleExpander(_serviceProvider);
        await expander.InitializeAsync();
        return expander;
    }

    private void SetupDefaultHierarchy()
    {
        _dbContext.RoleCompositions.AddRange(
            new RoleComposition { RoleName = "Guest", IncludesRole = "Anonymous" },
            new RoleComposition { RoleName = "User", IncludesRole = "Guest" },
            new RoleComposition { RoleName = "Operator", IncludesRole = "User" },
            new RoleComposition { RoleName = "Supervisor", IncludesRole = "Operator" },
            new RoleComposition { RoleName = "Admin", IncludesRole = "Supervisor" }
        );
        _dbContext.SaveChanges();
    }
}
