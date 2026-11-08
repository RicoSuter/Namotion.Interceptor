using System.Security.Claims;
using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class HomeBlazeClaimsPrincipalFactoryTests
{
    [Fact]
    public async Task WhenTheUserMustChangeTheirPassword_ThenThePrincipalSaysSo()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var admin = (await userManager.FindByNameAsync(AdminSeeding.AdminUserName))!;

        // Act
        var principal = await factory.CreateAsync(admin);

        // Assert
        Assert.True(admin.MustChangePassword);
        Assert.Equal("true", principal.FindFirst(HomeBlazeClaimTypes.MustChangePassword)?.Value);
    }

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

    [Fact]
    public async Task WhenTheUserNeedNotChangeTheirPassword_ThenThePrincipalHasNoSuchClaim()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var admin = (await userManager.FindByNameAsync(AdminSeeding.AdminUserName))!;
        admin.MustChangePassword = false;
        admin.AllowsEmptyPassword = false;

        // Act
        var principal = await factory.CreateAsync(admin);

        // Assert
        Assert.Null(principal.FindFirst(HomeBlazeClaimTypes.MustChangePassword));
    }

    [Fact]
    public async Task WhenTheUserAllowsAnEmptyPasswordWithoutTheStoredFlag_ThenThePrincipalMustChangeThePassword()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var admin = (await userManager.FindByNameAsync(AdminSeeding.AdminUserName))!;
        admin.MustChangePassword = false;

        // Act
        var principal = await factory.CreateAsync(admin);

        // Assert
        Assert.True(admin.AllowsEmptyPassword);
        Assert.Equal("true", principal.FindFirst(HomeBlazeClaimTypes.MustChangePassword)?.Value);
    }
}
