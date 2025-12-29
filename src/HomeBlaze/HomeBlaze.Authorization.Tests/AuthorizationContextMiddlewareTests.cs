using System.Security.Claims;
using HomeBlaze.Authorization.Context;
using HomeBlaze.Authorization.Middleware;
using HomeBlaze.Authorization.Roles;
using HomeBlaze.Authorization.Services;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

/// <summary>
/// Tests for AuthorizationContextMiddleware.
/// </summary>
public class AuthorizationContextMiddlewareTests
{
    public AuthorizationContextMiddlewareTests()
    {
        // Ensure clean state before each test
        AuthorizationContext.Clear();
    }

    [Fact]
    public async Task InvokeAsync_PopulatesContextFromHttpContextUser()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.Role, DefaultRoles.Admin));
        var user = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = user };
        var roleExpander = CreateMockRoleExpander();
        var middleware = new AuthorizationContextMiddleware(_ => Task.CompletedTask);

        ClaimsPrincipal? capturedUser = null;
        IReadOnlySet<string>? capturedRoles = null;

        var nextDelegate = new RequestDelegate(_ =>
        {
            capturedUser = AuthorizationContext.CurrentUser;
            capturedRoles = AuthorizationContext.ExpandedRoles;
            return Task.CompletedTask;
        });

        var middlewareWithCapture = new AuthorizationContextMiddleware(nextDelegate);

        // Act
        await middlewareWithCapture.InvokeAsync(httpContext, roleExpander);

        // Assert
        Assert.NotNull(capturedUser);
        Assert.Same(user, capturedUser);
        Assert.Contains(DefaultRoles.Admin, capturedRoles!);
    }

    [Fact]
    public async Task InvokeAsync_ClearsContextAfterRequest()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.Role, DefaultRoles.User));
        var user = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = user };
        var roleExpander = CreateMockRoleExpander();
        var middleware = new AuthorizationContextMiddleware(_ => Task.CompletedTask);

        // Act
        await middleware.InvokeAsync(httpContext, roleExpander);

        // Assert - context should be cleared after middleware completes
        Assert.Null(AuthorizationContext.CurrentUser);
        Assert.Empty(AuthorizationContext.ExpandedRoles);
    }

    [Fact]
    public async Task InvokeAsync_ClearsContextEvenOnException()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        var user = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = user };
        var roleExpander = CreateMockRoleExpander();
        var middleware = new AuthorizationContextMiddleware(_ => throw new InvalidOperationException("Test exception"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(httpContext, roleExpander));

        // Context should still be cleared
        Assert.Null(AuthorizationContext.CurrentUser);
        Assert.Empty(AuthorizationContext.ExpandedRoles);
    }

    [Fact]
    public async Task InvokeAsync_AddsAnonymousRoleForUnauthenticatedUsers()
    {
        // Arrange - unauthenticated user (no authentication type)
        var user = new ClaimsPrincipal(new ClaimsIdentity()); // No auth type = unauthenticated

        var httpContext = new DefaultHttpContext { User = user };
        var roleExpander = CreateMockRoleExpander();

        IReadOnlySet<string>? capturedRoles = null;

        var middleware = new AuthorizationContextMiddleware(_ =>
        {
            capturedRoles = AuthorizationContext.ExpandedRoles;
            return Task.CompletedTask;
        });

        // Act
        await middleware.InvokeAsync(httpContext, roleExpander);

        // Assert - Anonymous role should be added
        Assert.Contains(DefaultRoles.Anonymous, capturedRoles!);
    }

    [Fact]
    public async Task InvokeAsync_ExpandsRolesUsingRoleExpander()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.Role, DefaultRoles.Operator));
        var user = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = user };

        // Mock expander that adds inherited roles
        var expanderMock = new Mock<IRoleExpander>();
        expanderMock.Setup(e => e.ExpandRoles(It.IsAny<IEnumerable<string>>()))
            .Returns<IEnumerable<string>>(roles =>
            {
                var expanded = new HashSet<string>(roles);
                if (expanded.Contains(DefaultRoles.Operator))
                {
                    expanded.Add(DefaultRoles.User);
                    expanded.Add(DefaultRoles.Guest);
                }
                return expanded;
            });

        IReadOnlySet<string>? capturedRoles = null;

        var middleware = new AuthorizationContextMiddleware(_ =>
        {
            capturedRoles = AuthorizationContext.ExpandedRoles;
            return Task.CompletedTask;
        });

        // Act
        await middleware.InvokeAsync(httpContext, expanderMock.Object);

        // Assert - should have expanded roles
        Assert.Contains(DefaultRoles.Operator, capturedRoles!);
        Assert.Contains(DefaultRoles.User, capturedRoles!);
        Assert.Contains(DefaultRoles.Guest, capturedRoles!);
    }

    [Fact]
    public async Task InvokeAsync_CallsNextDelegate()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        var roleExpander = CreateMockRoleExpander();
        var nextCalled = false;

        var middleware = new AuthorizationContextMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        // Act
        await middleware.InvokeAsync(httpContext, roleExpander);

        // Assert
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ExtractsMultipleRoleClaims()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.Role, DefaultRoles.User));
        identity.AddClaim(new Claim(ClaimTypes.Role, DefaultRoles.Operator));
        identity.AddClaim(new Claim(ClaimTypes.Role, "CustomRole"));
        var user = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = user };
        var roleExpander = CreateMockRoleExpander();

        IReadOnlySet<string>? capturedRoles = null;

        var middleware = new AuthorizationContextMiddleware(_ =>
        {
            capturedRoles = AuthorizationContext.ExpandedRoles;
            return Task.CompletedTask;
        });

        // Act
        await middleware.InvokeAsync(httpContext, roleExpander);

        // Assert - all roles should be present
        Assert.Contains(DefaultRoles.User, capturedRoles!);
        Assert.Contains(DefaultRoles.Operator, capturedRoles!);
        Assert.Contains("CustomRole", capturedRoles!);
    }

    private static IRoleExpander CreateMockRoleExpander()
    {
        var expanderMock = new Mock<IRoleExpander>();
        expanderMock.Setup(e => e.ExpandRoles(It.IsAny<IEnumerable<string>>()))
            .Returns<IEnumerable<string>>(roles => roles.ToHashSet());
        return expanderMock.Object;
    }
}
