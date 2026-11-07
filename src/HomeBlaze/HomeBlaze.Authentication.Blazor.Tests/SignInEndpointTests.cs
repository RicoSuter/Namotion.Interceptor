using System.Net;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Testing;
using Xunit;
using static HomeBlaze.Authentication.Blazor.Tests.AccountTestForms;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class SignInEndpointTests
{
    [Fact]
    public async Task WhenTheSeededAdminSignsInWithAnEmptyPassword_ThenTheyAreSignedInAndMustChangeTheirPassword()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();

        // Act
        var response = await SignInAsync(browser, AdminSeeding.AdminUserName, string.Empty);

        // Assert
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(response));
        Assert.True(browser.HasSessionCookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/protected")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(AntiforgeryTestPage.Path)).StatusCode);
        Assert.NotNull(host.GetUser(AdminSeeding.AdminUserName).LastLogin);
    }

    [Fact]
    public async Task WhenTheSeededAdminSignsInWithAnEmptyPasswordAndAReturnUrl_ThenTheyLandOnTheRootToSetThePassword()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();

        // Act
        var response = await SignInAsync(browser, AdminSeeding.AdminUserName, string.Empty, "/protected?tab=1");

        // Assert
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData(" ADMIN ")]
    public async Task WhenTheUserNameIsTypedWithUpperCaseLetters_ThenTheLowerCaseUserSignsIn(string typedUserName)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();

        // Act
        var response = await SignInAsync(browser, typedUserName, string.Empty);

        // Assert
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(response));
        Assert.True(browser.HasSessionCookie);
        Assert.NotNull(host.GetUser(AdminSeeding.AdminUserName).LastLogin);
    }

    [Theory]
    [InlineData("adm\u0131n", "admin")]
    [InlineData("ADM\u0130N", "admin")]
    [InlineData("\u212Aid", "kid")]
    public async Task WhenTheUserNameIsTypedWithALetterOutsideAscii_ThenItIsRefusedAsAnUnknownUser(string typedUserName, string userName)
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var host = await AuthenticationTestHost.StartAsync(logger: logger);
        if (userName != AdminSeeding.AdminUserName)
        {
            var added = await host.AdministrationAsAdmin.AddUserAsync(userName, null, [BuiltInRoles.ReaderName], CancellationToken.None);
            Assert.True(added.Succeeded, added.Error);
        }

        await host.SetPasswordAsync(userName, Password);
        using var browser = host.CreateBrowser();
        var verificationsBefore = host.PasswordVerificationCount;

        // Act
        var response = await SignInAsync(browser, typedUserName, Password);

        // Assert
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(response));
        Assert.False(browser.HasSessionCookie);
        Assert.Equal(1, host.PasswordVerificationCount - verificationsBefore);
        Assert.Contains(logger.Warnings, message => message.StartsWith("A sign-in as (unknown user) from", StringComparison.Ordinal));
        Assert.Null(host.GetUser(userName).LastLogin);
    }

    [Fact]
    public async Task WhenAUserSignsInWithAnEmptyPasswordWithoutAllowsEmptyPassword_ThenItFails()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("mom", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();

        // Act
        var response = await SignInAsync(browser, "mom", string.Empty);

        // Assert
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(response));
        Assert.False(browser.HasSessionCookie);
    }

    [Theory]
    [InlineData(NameRules.AnonymousUserName)]
    [InlineData(NameRules.McpUserName)]
    public async Task WhenABuiltInUserSignsIn_ThenItFails(string userName)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();

        // Act
        var response = await SignInAsync(browser, userName, string.Empty);

        // Assert
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(response));
        Assert.False(browser.HasSessionCookie);
    }

    [Fact]
    public async Task WhenTheWrongPasswordIsEnteredFiveTimes_ThenTheAccountIsLockedAndTheMessageIsTheSameAsForAnUnknownUser()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var host = await AuthenticationTestHost.StartAsync(logger: logger);
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        using var browser = host.CreateBrowser();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(await SignInAsync(browser, AdminSeeding.AdminUserName, "wrong password")));
        }

        // Act
        var lockedOut = await SignInAsync(browser, AdminSeeding.AdminUserName, Password);
        var unknown = await SignInAsync(browser, "nobody", Password);

        // Assert
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(lockedOut));
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(unknown));
        Assert.False(browser.HasSessionCookie);
        Assert.True(host.GetUser(AdminSeeding.AdminUserName).IsLockedOut);
        Assert.Single(logger.Warnings, message => message == "The user admin is locked out after failed sign-ins.");
    }

    [Fact]
    public async Task WhenOthersFailToSignInAsASignedInAdministrator_ThenTheAdministratorKeepsManagingUsers()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        var administration = host.AdministrationAsAdmin;
        using var attacker = host.CreateBrowser();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await SignInAsync(attacker, AdminSeeding.AdminUserName, "wrong password");
        }

        // Act
        var added = await administration.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);

        // Assert
        Assert.True(host.GetUser(AdminSeeding.AdminUserName).IsLockedOut);
        Assert.True(added.Succeeded, added.Error);
    }

    [Fact]
    public async Task WhenMoreThanTenAttemptsComeFromOneAddressInAMinute_ThenTheyAreRateLimited()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await SignInAsync(browser, "nobody", "wrong password")).StatusCode);
        }

        // Act
        var limited = await SignInAsync(browser, "nobody", "wrong password");
        var setup = await RedeemSetupCodeAsync(browser, "nobody", "0000");

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, setup.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(AntiforgeryTestPage.Path)).StatusCode);
    }

    [Fact]
    public async Task WhenTheSignInHasNoAntiforgeryToken_ThenItIsRefusedWithoutSigningIn()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        using var browser = host.CreateBrowser();

        // Act
        var response = await browser.PostAccountFormAsync(AccountEndpoints.SignInPath, new Dictionary<string, string>
        {
            ["userName"] = AdminSeeding.AdminUserName,
            ["password"] = Password
        }, withToken: false);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(browser.HasSessionCookie);
        Assert.Null(host.GetUser(AdminSeeding.AdminUserName).LastLogin);
    }

    [Theory]
    [InlineData("/protected?tab=1", "/protected?tab=1")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("", "/")]
    public async Task WhenAUserSignsInWithAReturnUrl_ThenOnlyALocalOneIsFollowed(string returnUrl, string expected)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        using var browser = host.CreateBrowser();

        // Act
        var response = await SignInAsync(browser, AdminSeeding.AdminUserName, Password, returnUrl);

        // Assert
        Assert.Equal(expected, await TestBrowser.GetAccountRedirectAsync(response));
        Assert.True(browser.HasSessionCookie);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/protected")).StatusCode);
    }

    [Fact]
    public async Task WhenASignInIsAnswered_ThenTheAnswerIsNotCached()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();

        // Act
        var response = await SignInAsync(browser, "nobody", "wrong password");

        // Assert
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("wrong password", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WhenUnknownAndExistingUsersAreRefused_ThenEachVerifiesOnePasswordAndUnknownNamesAreNotLogged()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var host = await AuthenticationTestHost.StartAsync(logger: logger);
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        var added = await host.AdministrationAsAdmin.AddUserAsync("mom", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();
        (string UserName, string Password)[] attempts =
        [
            ("typed-secret", "wrong password"),
            ("typed-secret", string.Empty),
            (NameRules.McpUserName, "wrong password"),
            (AdminSeeding.AdminUserName, "wrong password"),
            (AdminSeeding.AdminUserName, string.Empty),
            ("mom", "wrong password"),
            ("mom", string.Empty)
        ];

        var verificationsByAttempt = new List<string>();
        var errors = new List<IReadOnlyList<string>>();

        // Act
        foreach (var (userName, password) in attempts)
        {
            var before = host.PasswordVerificationCount;
            var response = await SignInAsync(browser, userName, password);
            errors.Add(await TestBrowser.GetAccountErrorsAsync(response));
            verificationsByAttempt.Add($"{userName}/{password.Length > 0}: {host.PasswordVerificationCount - before}");
        }

        // Assert
        Assert.All(errors, error => Assert.Equal([InvalidSignIn], error));
        Assert.Equal(attempts.Select(attempt => $"{attempt.UserName}/{attempt.Password.Length > 0}: 1"), verificationsByAttempt);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("typed-secret", StringComparison.Ordinal));
        Assert.Contains(logger.Warnings, message => message.StartsWith("A sign-in as (unknown user) from", StringComparison.Ordinal));
        Assert.Contains(logger.Warnings, message => message.StartsWith("A sign-in as mom from", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheAdminHasAPasswordButStillAllowsAnEmptyOne_ThenAnEmptyPasswordIsRefused()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = (await userManager.FindByNameAsync(AdminSeeding.AdminUserName))!;
            Assert.True((await userManager.AddPasswordAsync(admin, Password)).Succeeded);
            Assert.True(admin.AllowsEmptyPassword);
        }

        using var browser = host.CreateBrowser();

        // Act
        var response = await SignInAsync(browser, AdminSeeding.AdminUserName, string.Empty);

        // Assert
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(response));
        Assert.False(browser.HasSessionCookie);
    }

    [Fact]
    public async Task WhenTheUserLogsOut_ThenTheCookieIsRemovedAndTheyAreSentToTheApp()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, AdminSeeding.AdminUserName);
        var fields = await browser.GetHiddenFieldsAsync(AntiforgeryTestPage.Path);

        // Act
        var response = await browser.PostAsync(AccountEndpoints.LogoutPath, fields);

        // Assert
        Assert.Equal("/", TestBrowser.GetRedirectPath(response));
        Assert.False(browser.HasSessionCookie);
        Assert.StartsWith("/?ReturnUrl=", TestBrowser.GetRedirectPath(await browser.GetAsync("/protected")));
    }

    [Fact]
    public async Task WhenTheUserLogsOutWithoutAnAntiforgeryToken_ThenItIsRefusedAndTheyStaySignedIn()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, AdminSeeding.AdminUserName);

        // Act
        var response = await browser.PostAsync(AccountEndpoints.LogoutPath, new Dictionary<string, string>());

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(browser.HasSessionCookie);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/protected")).StatusCode);
    }

    [Fact]
    public async Task WhenAVisitorWhoIsNotSignedInLogsOut_ThenTheyAreSentToTheAppWithoutAToken()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();

        // Act
        var response = await browser.PostAsync(AccountEndpoints.LogoutPath, new Dictionary<string, string>());

        // Assert
        Assert.Equal("/", TestBrowser.GetRedirectPath(response));
    }

    [Fact]
    public async Task WhenAttemptsComeFromOneIPv6Network_ThenTheyShareOneLimit()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.NotEqual(StatusCodes.Status429TooManyRequests, (await PostSignInFromAsync(host, IPAddress.Parse($"2001:db8::{attempt + 1}"))).Response.StatusCode);
        }

        // Act
        var sameNetwork = await PostSignInFromAsync(host, IPAddress.Parse("2001:db8::ffff"));
        var otherNetwork = await PostSignInFromAsync(host, IPAddress.Parse("2001:db8:0:1::1"));
        var ipv4 = await PostSignInFromAsync(host, IPAddress.Parse("192.0.2.1"));

        // Assert
        Assert.Equal(StatusCodes.Status429TooManyRequests, sameNetwork.Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, otherNetwork.Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, ipv4.Response.StatusCode);
    }

    [Fact]
    public async Task WhenAttemptsComeFromIPv4MappedAddresses_ThenEachIPv4AddressHasItsOwnLimit()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.NotEqual(StatusCodes.Status429TooManyRequests, (await PostSignInFromAsync(host, IPAddress.Parse("::ffff:192.0.2.1"))).Response.StatusCode);
        }

        // Act
        var sameAddress = await PostSignInFromAsync(host, IPAddress.Parse("192.0.2.1"));
        var otherAddress = await PostSignInFromAsync(host, IPAddress.Parse("::ffff:192.0.2.2"));

        // Assert
        Assert.Equal(StatusCodes.Status429TooManyRequests, sameAddress.Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, otherAddress.Response.StatusCode);
    }

    [Fact]
    public async Task WhenAttemptsHaveNoClientAddress_ThenAStricterSharedLimitAppliesWithOneWarning()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var host = await AuthenticationTestHost.StartAsync(logger: logger);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.NotEqual(StatusCodes.Status429TooManyRequests, (await PostSignInFromAsync(host, null)).Response.StatusCode);
        }

        // Act
        var limited = await PostSignInFromAsync(host, null);

        // Assert
        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.Response.StatusCode);
        Assert.Single(logger.Warnings, message => message.Contains("without a client address", StringComparison.Ordinal));
    }

    private static Task<HttpContext> PostSignInFromAsync(AuthenticationTestHost host, IPAddress? address) =>
        host.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = AccountEndpoints.SignInPath;
            context.Connection.RemoteIpAddress = address;
        });
}
