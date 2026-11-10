using System.Security.Claims;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class HomeBlazeAccessHandlerTests
{
    [Fact]
    public async Task WhenThePersonIsSignedIn_ThenAccessIsGranted()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], authenticationType: "Test"));

        // Act
        var succeeded = await AuthorizeAsync(host, user);

        // Assert
        Assert.True(succeeded);
    }

    [Fact]
    public async Task WhenNobodyIsSignedInAndAnonymousHasNoRoles_ThenAccessIsDenied()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();

        // Act
        var succeeded = await AuthorizeAsync(host, new ClaimsPrincipal(new ClaimsIdentity()));

        // Assert
        Assert.False(succeeded);
    }

    [Fact]
    public async Task WhenNobodyIsSignedInAndAnonymousHasRoles_ThenAccessIsGranted()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(anonymousRoles: [BuiltInRoles.ReaderName]);

        // Act
        var succeeded = await AuthorizeAsync(host, new ClaimsPrincipal(new ClaimsIdentity()));

        // Assert
        Assert.True(succeeded);
    }

    private static async Task<bool> AuthorizeAsync(AuthenticationTestHost host, ClaimsPrincipal user)
    {
        var service = host.Services.GetRequiredService<IAuthorizationService>();
        var result = await service.AuthorizeAsync(user, resource: null, HomeBlazeAccessRequirement.PolicyName);
        return result.Succeeded;
    }
}
