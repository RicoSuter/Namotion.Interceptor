using System.Security.Claims;
using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class SessionPrincipalTests
{
    [Fact]
    public async Task WhenAPersonWithRolesSignsIn_ThenThePrincipalHasNoRoleClaimsButItsStamp()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
        var admin = (await userManager.FindByNameAsync(AdminSeeding.AdminUserName))!;

        // Act
        var principal = await factory.CreateAsync(admin);

        // Assert
        Assert.Contains(HomeBlaze.Authorization.BuiltInRoles.EverythingName, await userManager.GetRolesAsync(admin));
        Assert.Empty(principal.FindAll(options.RoleClaimType));
        Assert.Empty(principal.FindAll(ClaimTypes.Role));
        Assert.Equal(admin.Id, principal.FindFirst(options.UserIdClaimType)?.Value);
        Assert.Equal(await userManager.GetSecurityStampAsync(admin), principal.FindFirst(options.SecurityStampClaimType)?.Value);
    }
}
