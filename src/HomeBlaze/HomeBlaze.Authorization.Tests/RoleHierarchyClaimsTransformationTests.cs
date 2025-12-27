using System.Security.Claims;
using HomeBlaze.Authorization.Services;
using Moq;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

/// <summary>
/// Tests for RoleHierarchyClaimsTransformation.
/// </summary>
public class RoleHierarchyClaimsTransformationTests
{
    private readonly Mock<IRoleExpander> _expanderMock;
    private readonly RoleHierarchyClaimsTransformation _transformation;

    public RoleHierarchyClaimsTransformationTests()
    {
        _expanderMock = new Mock<IRoleExpander>();
        _transformation = new RoleHierarchyClaimsTransformation(_expanderMock.Object);
    }

    [Fact]
    public async Task TransformAsync_UnauthenticatedPrincipal_ReturnsUnchanged()
    {
        // Arrange
        var identity = new ClaimsIdentity(); // Not authenticated (no auth type)
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await _transformation.TransformAsync(principal);

        // Assert
        Assert.Same(principal, result);
        _expanderMock.Verify(e => e.ExpandRoles(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task TransformAsync_NoRoleClaims_ReturnsUnchanged()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.Name, "testuser"));
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await _transformation.TransformAsync(principal);

        // Assert
        Assert.Same(principal, result);
        _expanderMock.Verify(e => e.ExpandRoles(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task TransformAsync_WithRoleClaims_ExpandsRoles()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        var principal = new ClaimsPrincipal(identity);

        _expanderMock.Setup(e => e.ExpandRoles(It.IsAny<IEnumerable<string>>()))
            .Returns(new HashSet<string> { "Admin", "Supervisor", "User" });

        // Act
        var result = await _transformation.TransformAsync(principal);

        // Assert
        Assert.True(result.IsInRole("Admin"));
        Assert.True(result.IsInRole("Supervisor"));
        Assert.True(result.IsInRole("User"));
    }

    [Fact]
    public async Task TransformAsync_DoesNotDuplicateExistingRoles()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "User"));
        var principal = new ClaimsPrincipal(identity);

        _expanderMock.Setup(e => e.ExpandRoles(It.IsAny<IEnumerable<string>>()))
            .Returns(new HashSet<string> { "Admin", "User", "Guest" });

        // Act
        var result = await _transformation.TransformAsync(principal);

        // Assert - should only have 3 role claims (Admin, User already existed, Guest added)
        var roleClaims = result.FindAll(ClaimTypes.Role).ToList();
        Assert.Equal(3, roleClaims.Count);
    }

    [Fact]
    public async Task TransformAsync_PassesCorrectRolesToExpander()
    {
        // Arrange
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "CustomRole"));
        var principal = new ClaimsPrincipal(identity);

        IEnumerable<string>? capturedRoles = null;
        _expanderMock.Setup(e => e.ExpandRoles(It.IsAny<IEnumerable<string>>()))
            .Callback<IEnumerable<string>>(roles => capturedRoles = roles.ToList())
            .Returns(new HashSet<string> { "Admin", "CustomRole" });

        // Act
        await _transformation.TransformAsync(principal);

        // Assert
        Assert.NotNull(capturedRoles);
        Assert.Contains("Admin", capturedRoles);
        Assert.Contains("CustomRole", capturedRoles);
    }
}
