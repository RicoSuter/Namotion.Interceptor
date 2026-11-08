using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class SessionValidationTests
{
    [Fact]
    public async Task WhenTheDatabaseIsNotReady_ThenTheSessionIsRejectedWithoutCheckingTheStoredStamp()
    {
        // Arrange
        var validator = new RecordingSecurityStampValidator();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddSessionValidation();
        services.AddSingleton(new AuthenticationDatabase("unused.db", () => throw new NotSupportedException(), NullLogger<AuthenticationDatabase>.Instance));
        services.AddSingleton<ISecurityStampValidator>(validator);
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var scheme = new AuthenticationScheme(IdentityConstants.ApplicationScheme, displayName: null, typeof(CookieAuthenticationHandler));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-id")], authenticationType: "Test"));
        var context = new CookieValidatePrincipalContext(
            new DefaultHttpContext { RequestServices = provider }, scheme, options, new AuthenticationTicket(principal, scheme.Name));

        // Act
        await options.Events.ValidatePrincipal(context);

        // Assert
        Assert.Null(context.Principal);
        Assert.False(validator.IsCalled);
    }

    private sealed class RecordingSecurityStampValidator : ISecurityStampValidator
    {
        public bool IsCalled { get; private set; }

        public Task ValidateAsync(CookieValidatePrincipalContext context)
        {
            IsCalled = true;
            return Task.CompletedTask;
        }
    }
}
