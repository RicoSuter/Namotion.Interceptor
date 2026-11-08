using System.Net;
using HomeBlaze.Authorization;
using Namotion.Interceptor.Testing;
using Xunit;
using static HomeBlaze.Authentication.Blazor.Tests.AccountTestForms;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class DisplayNameEndpointTests
{
    [Fact]
    public async Task WhenASignedInPersonSetsTheirDisplayName_ThenItIsStoredLoggedAndTheSessionStays()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var host = await AuthenticationTestHost.StartAsync(logger: logger);
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        using var browser = host.CreateBrowser();
        await SignInAsync(browser, AdminSeeding.AdminUserName, Password);

        // Act
        var response = await SetDisplayNameAsync(browser, " Rémi Admin ", "/pages/a");

        // Assert
        Assert.Equal("/pages/a", await TestBrowser.GetAccountRedirectAsync(response));
        Assert.Equal("Rémi Admin", host.GetUser(AdminSeeding.AdminUserName).DisplayName);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/protected")).StatusCode);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("display name", StringComparison.Ordinal) && entry.Message.Contains(AdminSeeding.AdminUserName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheDisplayNameIsInvalid_ThenTheErrorIsAnsweredAndNothingIsStored()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        using var browser = host.CreateBrowser();
        await SignInAsync(browser, AdminSeeding.AdminUserName, Password);

        // Act
        var response = await SetDisplayNameAsync(browser, "Admin‮");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["The display name contains a control, invisible, private use or unassigned character."], await TestBrowser.GetAccountErrorsAsync(response));
        Assert.Null(host.GetUser(AdminSeeding.AdminUserName).DisplayName);
    }

    [Fact]
    public async Task WhenTheDisplayNameIsPostedWithoutAnAntiforgeryToken_ThenNothingIsStored()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        using var browser = host.CreateBrowser();
        await SignInAsync(browser, AdminSeeding.AdminUserName, Password);

        // Act
        var response = await SetDisplayNameAsync(browser, "Admin", withToken: false);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(host.GetUser(AdminSeeding.AdminUserName).DisplayName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenTheSessionWasEndedByAResetOrALock_ThenItCannotSetTheDisplayName(bool isLock)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        await host.SetPasswordAsync("kid", Password);
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, "kid");
        if (isLock)
        {
            // Directly in the database, as a lock that the stamp check alone would not notice.
            await host.LockOutAsync("kid", DateTimeOffset.MaxValue);
        }
        else
        {
            var reset = await host.AdministrationAsAdmin.ResetPasswordAsync("kid", CancellationToken.None);
            Assert.True(reset.Succeeded, reset.Error);
        }

        // Act
        var response = await SetDisplayNameAsync(browser, "Kid");

        // Assert
        // The user store knows of the reset, so the cookie is refused before the endpoint, which checks the stamp itself.
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(response, isLock ? HttpStatusCode.OK : HttpStatusCode.Unauthorized));
        // The endpoint signs out the session it finds locked; a cookie refused before it is kept, as every request refuses it.
        Assert.Equal(!isLock, browser.HasSessionCookie);
        Assert.Null(host.UserStore.GetDisplayName(host.UserStore.GetUserId("kid")!));
    }

    [Fact]
    public async Task WhenAVisitorWhoIsNotSignedInPostsADisplayName_ThenTheyGet401WithTheRootToLoad()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();

        // Act
        var response = await SetDisplayNameAsync(browser, "Admin");

        // Assert
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(response, HttpStatusCode.Unauthorized));
        Assert.Null(host.GetUser(AdminSeeding.AdminUserName).DisplayName);
    }

    [Fact]
    public async Task WhenTheDisplayNameIsPostedMoreThanTenTimesInAMinute_ThenItIsRateLimited()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        using var browser = host.CreateBrowser();
        // The sign-in is the first of the ten posts the limit allows.
        await SignInAsync(browser, AdminSeeding.AdminUserName, Password);
        for (var attempt = 0; attempt < 9; attempt++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await SetDisplayNameAsync(browser, "Admin\u0007")).StatusCode);
        }

        // Act
        var limited = await SetDisplayNameAsync(browser, "Admin");

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Null(host.GetUser(AdminSeeding.AdminUserName).DisplayName);
    }
}
