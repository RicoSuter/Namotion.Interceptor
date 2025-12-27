using System.Security.Claims;
using HomeBlaze.Authorization.Context;
using HomeBlaze.Authorization.Roles;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

/// <summary>
/// Tests for AuthorizationContext static context management.
/// </summary>
public class AuthorizationContextTests
{
    public AuthorizationContextTests()
    {
        // Ensure clean state before each test
        AuthorizationContext.Clear();
    }

    [Fact]
    public void CurrentUser_WhenNotSet_ReturnsNull()
    {
        // Assert
        Assert.Null(AuthorizationContext.CurrentUser);
    }

    [Fact]
    public void ExpandedRoles_WhenNotSet_ReturnsEmptySet()
    {
        // Assert
        Assert.Empty(AuthorizationContext.ExpandedRoles);
    }

    [Fact]
    public void IsAuthenticated_WhenNotSet_ReturnsFalse()
    {
        // Assert
        Assert.False(AuthorizationContext.IsAuthenticated);
    }

    [Fact]
    public void SetUser_SetsCurrentUser()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.Name, "testuser"));
        var user = new ClaimsPrincipal(identity);

        // Act
        AuthorizationContext.SetUser(user, [DefaultRoles.User]);

        // Assert
        Assert.NotNull(AuthorizationContext.CurrentUser);
        Assert.Equal("testuser", AuthorizationContext.CurrentUser.Identity?.Name);
    }

    [Fact]
    public void SetUser_SetsExpandedRoles()
    {
        // Arrange
        var user = new ClaimsPrincipal(new ClaimsIdentity("TestAuth"));
        var roles = new[] { DefaultRoles.Admin, DefaultRoles.Supervisor, DefaultRoles.Operator };

        // Act
        AuthorizationContext.SetUser(user, roles);

        // Assert
        Assert.Contains(DefaultRoles.Admin, AuthorizationContext.ExpandedRoles);
        Assert.Contains(DefaultRoles.Supervisor, AuthorizationContext.ExpandedRoles);
        Assert.Contains(DefaultRoles.Operator, AuthorizationContext.ExpandedRoles);
    }

    [Fact]
    public void IsAuthenticated_WhenUserSet_ReturnsTrue()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        var user = new ClaimsPrincipal(identity);

        // Act
        AuthorizationContext.SetUser(user, []);

        // Assert
        Assert.True(AuthorizationContext.IsAuthenticated);
    }

    [Fact]
    public void Clear_RemovesUserAndRoles()
    {
        // Arrange
        var user = new ClaimsPrincipal(new ClaimsIdentity("TestAuth"));
        AuthorizationContext.SetUser(user, [DefaultRoles.Admin]);

        // Act
        AuthorizationContext.Clear();

        // Assert
        Assert.Null(AuthorizationContext.CurrentUser);
        Assert.Empty(AuthorizationContext.ExpandedRoles);
        Assert.False(AuthorizationContext.IsAuthenticated);
    }

    [Fact]
    public void HasAnyRole_WhenUserHasRole_ReturnsTrue()
    {
        // Arrange
        var user = new ClaimsPrincipal(new ClaimsIdentity("TestAuth"));
        AuthorizationContext.SetUser(user, [DefaultRoles.Operator, DefaultRoles.User, DefaultRoles.Guest]);

        // Act & Assert
        Assert.True(AuthorizationContext.HasAnyRole([DefaultRoles.Operator]));
        Assert.True(AuthorizationContext.HasAnyRole([DefaultRoles.Guest]));
        Assert.True(AuthorizationContext.HasAnyRole([DefaultRoles.Admin, DefaultRoles.User])); // Has User
    }

    [Fact]
    public void HasAnyRole_WhenUserLacksRole_ReturnsFalse()
    {
        // Arrange
        var user = new ClaimsPrincipal(new ClaimsIdentity("TestAuth"));
        AuthorizationContext.SetUser(user, [DefaultRoles.Guest]);

        // Act & Assert
        Assert.False(AuthorizationContext.HasAnyRole([DefaultRoles.Admin]));
        Assert.False(AuthorizationContext.HasAnyRole([DefaultRoles.Supervisor, DefaultRoles.Operator]));
    }

    [Fact]
    public void HasAnyRole_WhenNoUser_ReturnsFalse()
    {
        // Act & Assert
        Assert.False(AuthorizationContext.HasAnyRole([DefaultRoles.Guest]));
        Assert.False(AuthorizationContext.HasAnyRole([DefaultRoles.Anonymous]));
    }

    [Fact]
    public async Task Context_FlowsAcrossAsyncCalls()
    {
        // Arrange
        var user = new ClaimsPrincipal(new ClaimsIdentity("TestAuth"));
        AuthorizationContext.SetUser(user, [DefaultRoles.Admin]);

        // Act - access context after async operation
        await Task.Delay(1);
        var hasRole = AuthorizationContext.HasAnyRole([DefaultRoles.Admin]);

        // Assert
        Assert.True(hasRole);
    }

    [Fact]
    public async Task Context_IsIsolatedBetweenAsyncFlows()
    {
        // Arrange & Act
        var task1 = Task.Run(() =>
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity("TestAuth"));
            AuthorizationContext.SetUser(user, [DefaultRoles.Admin]);
            Thread.Sleep(50);
            return AuthorizationContext.HasAnyRole([DefaultRoles.Admin]);
        });

        var task2 = Task.Run(() =>
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity("TestAuth"));
            AuthorizationContext.SetUser(user, [DefaultRoles.Guest]);
            Thread.Sleep(50);
            return AuthorizationContext.HasAnyRole([DefaultRoles.Guest]);
        });

        var results = await Task.WhenAll(task1, task2);

        // Assert - each task should see its own context
        Assert.True(results[0]); // Task 1 has Admin
        Assert.True(results[1]); // Task 2 has Guest
    }
}
