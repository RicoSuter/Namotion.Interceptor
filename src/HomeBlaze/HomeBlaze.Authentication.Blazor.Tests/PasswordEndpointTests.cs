using System.Net;
using HomeBlaze.Authorization;
using Xunit;
using static HomeBlaze.Authentication.Blazor.Tests.AccountTestForms;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class PasswordEndpointTests
{
    private const string AnotherPassword = "another good password";

    [Fact]
    public async Task WhenTheSeededAdminSetsAPassword_ThenTheSessionIsRefreshedAndAnEmptyPasswordIsRefusedAfterwards()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();
        await SignInAsync(browser, AdminSeeding.AdminUserName, string.Empty);

        // Act
        var response = await SetPasswordAsync(browser, Password);

        // Assert
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(response));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/protected")).StatusCode);
        Assert.False(host.GetUser(AdminSeeding.AdminUserName).MustChangePassword);

        using var emptyPasswordBrowser = host.CreateBrowser();
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(await SignInAsync(emptyPasswordBrowser, AdminSeeding.AdminUserName, string.Empty)));
        Assert.False(emptyPasswordBrowser.HasSessionCookie);

        using var passwordBrowser = host.CreateBrowser();
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(await SignInAsync(passwordBrowser, AdminSeeding.AdminUserName, Password)));
        Assert.True(passwordBrowser.HasSessionCookie);
    }

    [Theory]
    [InlineData("/pages/a?tab=1", "/pages/a?tab=1")]
    [InlineData("https://evil.example/", "/")]
    public async Task WhenThePasswordIsChanged_ThenOnlyALocalReturnUrlIsFollowed(string returnUrl, string expected)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        using var browser = host.CreateBrowser();
        await SignInAsync(browser, AdminSeeding.AdminUserName, Password);

        // Act
        var response = await browser.PostAccountFormAsync(AccountEndpoints.PasswordPath, new Dictionary<string, string>
        {
            ["currentPassword"] = Password,
            ["newPassword"] = AnotherPassword,
            ["confirmPassword"] = AnotherPassword,
            ["returnUrl"] = returnUrl
        });

        // Assert
        Assert.Equal(expected, await TestBrowser.GetAccountRedirectAsync(response));
    }

    [Fact]
    public async Task WhenThePasswordIsChanged_ThenTheOldPasswordAndTheOtherSessionsAreRefused()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        using var browser = host.CreateBrowser();
        await SignInAsync(browser, AdminSeeding.AdminUserName, Password);
        using var otherBrowser = host.CreateBrowser();
        await SignInAsync(otherBrowser, AdminSeeding.AdminUserName, Password);

        // Act
        var response = await SetPasswordAsync(browser, AnotherPassword, currentPassword: Password);

        // Assert
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(response));
        Assert.True(browser.HasSessionCookie);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/protected")).StatusCode);

        // The old session is checked against the stamp by the password endpoint at once.
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(await SetPasswordAsync(otherBrowser, "a password of the attacker", currentPassword: AnotherPassword)));
        Assert.False(otherBrowser.HasSessionCookie);

        using var oldPasswordBrowser = host.CreateBrowser();
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(await SignInAsync(oldPasswordBrowser, AdminSeeding.AdminUserName, Password)));
        using var newPasswordBrowser = host.CreateBrowser();
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(await SignInAsync(newPasswordBrowser, AdminSeeding.AdminUserName, AnotherPassword)));
    }

    [Fact]
    public async Task WhenTheNewPasswordsDiffer_ThenTheyAreRefusedWithAMessage()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();
        await SignInAsync(browser, AdminSeeding.AdminUserName, string.Empty);

        // Act
        var response = await SetPasswordAsync(browser, Password, confirmPassword: "something else");

        // Assert
        Assert.Equal(["The new passwords do not match."], await TestBrowser.GetAccountErrorsAsync(response));
        Assert.True(host.GetUser(AdminSeeding.AdminUserName).MustChangePassword);
    }

    [Fact]
    public async Task WhenTheNewPasswordIsTooShort_ThenThePolicyErrorIsShownAndNothingIsStored()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();
        await SignInAsync(browser, AdminSeeding.AdminUserName, string.Empty);

        // Act
        var response = await SetPasswordAsync(browser, "short");

        // Assert
        var error = Assert.Single(await TestBrowser.GetAccountErrorsAsync(response));
        Assert.Contains("10", error, StringComparison.Ordinal);
        Assert.False(await HasPasswordAsync(host, AdminSeeding.AdminUserName));
    }

    [Fact]
    public async Task WhenThePasswordIsPostedWithoutAnAntiforgeryToken_ThenNothingIsStored()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();
        await SignInAsync(browser, AdminSeeding.AdminUserName, string.Empty);

        // Act
        var response = await browser.PostAccountFormAsync(AccountEndpoints.PasswordPath, new Dictionary<string, string>
        {
            ["newPassword"] = Password,
            ["confirmPassword"] = Password
        }, withToken: false);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await HasPasswordAsync(host, AdminSeeding.AdminUserName));
        Assert.True(browser.HasSessionCookie);
    }

    [Fact]
    public async Task WhenAVisitorWhoIsNotSignedInPostsAPassword_ThenTheyGet401InsteadOfARedirect()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();

        // Act
        var response = await SetPasswordAsync(browser, Password);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheCurrentPasswordIsWrongFiveTimes_ThenTheUserIsLockedOutAndSignedOut()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        using var browser = host.CreateBrowser();
        await SignInAsync(browser, AdminSeeding.AdminUserName, Password);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.Equal(["Incorrect password."], await TestBrowser.GetAccountErrorsAsync(await SetPasswordAsync(browser, AnotherPassword, currentPassword: "wrong password")));
        }

        // Act
        var lockedOut = await SetPasswordAsync(browser, AnotherPassword, currentPassword: "wrong password");

        // Assert
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(lockedOut));
        Assert.False(browser.HasSessionCookie);
        Assert.True(host.GetUser(AdminSeeding.AdminUserName).IsLockedOut);
    }

    [Fact]
    public async Task WhenThePasswordIsPostedMoreThanTenTimesInAMinute_ThenItIsRateLimited()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();
        // The sign-in is the first of the ten posts the limit allows.
        await SignInAsync(browser, AdminSeeding.AdminUserName, string.Empty);
        for (var attempt = 0; attempt < 9; attempt++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(browser, Password, confirmPassword: "something else")).StatusCode);
        }

        // Act
        var limited = await SetPasswordAsync(browser, Password);

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(host.GetUser(AdminSeeding.AdminUserName).MustChangePassword);
    }

    [Fact]
    public async Task WhenAUserWithAPasswordIsLockedOutByFailedSignInsElsewhere_ThenChangingThePasswordIsRefusedAndTheSessionKept()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        using var browser = host.CreateBrowser();
        await SignInAsync(browser, AdminSeeding.AdminUserName, Password);
        using var attacker = host.CreateBrowser();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await SignInAsync(attacker, AdminSeeding.AdminUserName, "wrong password");
        }

        // Act
        var response = await SetPasswordAsync(browser, AnotherPassword, currentPassword: Password);

        // Assert
        Assert.True(host.GetUser(AdminSeeding.AdminUserName).IsLockedOut);
        Assert.Equal([PasswordChange.LockedOutError], await TestBrowser.GetAccountErrorsAsync(response));
        Assert.True(browser.HasSessionCookie);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(BannersTestPage.Path)).StatusCode);
    }

    [Fact]
    public async Task WhenTheAdminWithoutAPasswordIsLockedOut_ThenTheyCanStillSetTheirFirstPassword()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();
        await SignInAsync(browser, AdminSeeding.AdminUserName, string.Empty);
        await host.LockOutAsync(AdminSeeding.AdminUserName);

        // Act
        var response = await SetPasswordAsync(browser, Password);

        // Assert
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(response));
        Assert.True(browser.HasSessionCookie);
        Assert.False(host.GetUser(AdminSeeding.AdminUserName).MustChangePassword);
    }

    [Fact]
    public async Task WhenThePasswordIsResetWhileSignedIn_ThenTheOldSessionCannotSetAPassword()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        await host.SetPasswordAsync("kid", Password);
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, "kid");
        var reset = await host.AdministrationAsAdmin.ResetPasswordAsync("kid", CancellationToken.None);
        Assert.True(reset.Succeeded, reset.Error);

        // Act
        var response = await SetPasswordAsync(browser, "a password of the attacker");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(browser.HasSessionCookie);
        Assert.False(await HasPasswordAsync(host, "kid"));
    }

    [Fact]
    public async Task WhenAUserWithoutAPasswordIsLockedByAnAdministrator_ThenTheyCannotSetAPassword()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();
        await SignInAsync(browser, AdminSeeding.AdminUserName, string.Empty);
        // Directly in the database, as a lock that the stamp check alone would not notice.
        await host.LockOutAsync(AdminSeeding.AdminUserName, DateTimeOffset.MaxValue);

        // Act
        var response = await SetPasswordAsync(browser, Password);

        // Assert
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(response));
        Assert.False(browser.HasSessionCookie);
        Assert.False(await HasPasswordAsync(host, AdminSeeding.AdminUserName));
    }
}
