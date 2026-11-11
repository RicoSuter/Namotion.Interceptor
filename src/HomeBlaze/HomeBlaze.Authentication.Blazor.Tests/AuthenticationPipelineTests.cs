using System.Net;
using System.Net.Http.Headers;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class AuthenticationPipelineTests
{
    [Theory]
    [InlineData("reset")]
    [InlineData("lock")]
    [InlineData("remove")]
    public async Task WhenTheSessionOfAPersonEnds_ThenAProtectedEndpointRefusesItsCookieAtOnce(string change)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, "kid");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/protected")).StatusCode);
        var administration = host.AdministrationAsAdmin;
        var result = change switch
        {
            "reset" => await administration.ResetPasswordAsync("kid", CancellationToken.None),
            "lock" => await administration.SetLockedAsync("kid", true, CancellationToken.None),
            _ => await administration.RemoveAsync("kid", CancellationToken.None)
        };

        // Act
        var response = await browser.GetAsync("/protected");

        // Assert
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Theory]
    [InlineData("unlock")]
    [InlineData("redeem")]
    public async Task WhenAPersonIsActiveAgainAfterALockOrAReset_ThenACookieFromBeforeIsStillRefused(string change)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var oldBrowser = host.CreateBrowser();
        await host.SignInAsync(oldBrowser, "kid");
        using var newBrowser = host.CreateBrowser();
        var administration = host.AdministrationAsAdmin;
        if (change == "unlock")
        {
            Assert.True((await administration.SetLockedAsync("kid", true, CancellationToken.None)).Succeeded);
            Assert.True((await administration.SetLockedAsync("kid", false, CancellationToken.None)).Succeeded);
            await host.SignInAsync(newBrowser, "kid");
        }
        else
        {
            var reset = await administration.ResetPasswordAsync("kid", CancellationToken.None);
            Assert.True(reset.Succeeded, reset.Error);
            Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(await AccountTestForms.RedeemSetupCodeAsync(newBrowser, "kid", reset.SetupCode!)));
        }

        // Act
        var oldResponse = await oldBrowser.GetAsync("/protected");
        var newResponse = await newBrowser.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.Redirect, oldResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newResponse.StatusCode);
    }

    [Fact]
    public async Task WhenAPersonChangesTheirPassword_ThenTheCookiesOfTheirOtherSessionsAreRefusedAtOnce()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        await host.SetPasswordAsync("kid", AccountTestForms.Password);
        using var changingBrowser = host.CreateBrowser();
        using var otherBrowser = host.CreateBrowser();
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(await AccountTestForms.SignInAsync(changingBrowser, "kid", AccountTestForms.Password)));
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(await AccountTestForms.SignInAsync(otherBrowser, "kid", AccountTestForms.Password)));
        var changed = await AccountTestForms.SetPasswordAsync(changingBrowser, "another correct horse", currentPassword: AccountTestForms.Password);
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(changed));

        // Act
        var changingResponse = await changingBrowser.GetAsync("/protected");
        var otherResponse = await otherBrowser.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.OK, changingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, otherResponse.StatusCode);
    }

    [Fact]
    public async Task WhenTheUserStoreRefusesACookieUntilItHoldsTheStampOfTheCookieAgain_ThenTheSessionAnswersAgain()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, "kid");
        // As while the users load at startup or right after a password change, before the browser stored the new cookie.
        var securityStamp = await SetStoredSecurityStampAsync(host, "kid", "another stamp");
        var refused = await browser.GetAsync("/protected");
        await SetStoredSecurityStampAsync(host, "kid", securityStamp);

        // Act
        var response = await browser.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WhenARequestCarriesTheCookieFromBeforeAPasswordChangeAfterTheStampInterval_ThenItIsRefusedAndNoCookieIsDeleted()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await AuthenticationTestHost.StartAsync(time);
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        await host.SetPasswordAsync("kid", AccountTestForms.Password);
        using var browser = host.CreateBrowser();
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(await AccountTestForms.SignInAsync(browser, "kid", AccountTestForms.Password)));
        var oldCookie = browser.SessionCookieValue;
        time.Advance(TimeSpan.FromMinutes(6));
        var changed = await AccountTestForms.SetPasswordAsync(browser, "another correct horse", currentPassword: AccountTestForms.Password);
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(changed));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/protected");
        request.Headers.Add("Cookie", $"{TestBrowser.SessionCookieName}={oldCookie}");

        // Act
        var response = await host.CreateClient().SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var setCookies) &&
            setCookies.Any(setCookie => setCookie.StartsWith(TestBrowser.SessionCookieName + "=", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/protected")).StatusCode);
    }

    [Fact]
    public async Task WhenACookieCarriesAStampTheStoreMissed_ThenTheStoreReloadsTheUserAndALaterRequestIsServed()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, "kid");
        // As a password change whose reload of the store failed after the commit, which signed the browser in again.
        await host.ChangeSecurityStampAsync("kid");
        var (cookie, newStamp) = await CreateSessionCookieAsync(host, "kid");
        var userId = host.UserStore.GetUserId("kid")!;
        using var refusedRequest = new HttpRequestMessage(HttpMethod.Get, "/protected");
        refusedRequest.Headers.Add("Cookie", cookie);

        // Act
        var refused = await host.CreateClient().SendAsync(refusedRequest);
        await AsyncTestHelpers.WaitUntilAsync(() => host.UserStore.IsSessionAllowed(userId, newStamp));
        using var laterRequest = new HttpRequestMessage(HttpMethod.Get, "/protected");
        laterRequest.Headers.Add("Cookie", cookie);
        var served = await host.CreateClient().SendAsync(laterRequest);

        // Assert
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/protected")).StatusCode);
    }

    [Fact]
    public async Task WhenARefusedCookieFromBeforeAPasswordChangeIsPastHalfItsLifetime_ThenItIsNotRenewed()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await AuthenticationTestHost.StartAsync(time);
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        await host.SetPasswordAsync("kid", AccountTestForms.Password);
        using var browser = host.CreateBrowser();
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(await AccountTestForms.SignInAsync(browser, "kid", AccountTestForms.Password)));
        var oldCookie = browser.SessionCookieValue;
        var changed = await AccountTestForms.SetPasswordAsync(browser, "another correct horse", currentPassword: AccountTestForms.Password);
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(changed));
        time.Advance(TimeSpan.FromDays(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/protected");
        request.Headers.Add("Cookie", $"{TestBrowser.SessionCookieName}={oldCookie}");

        // Act
        var response = await host.CreateClient().SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var setCookies) &&
            setCookies.Any(setCookie => setCookie.StartsWith(TestBrowser.SessionCookieName + "=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WhenARequestAcceptedBeforeAPasswordChangeAnswersAfterIt_ThenItsRenewedCookieDoesNotReplaceTheNewOne()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await AuthenticationTestHost.StartAsync(time);
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        await host.SetPasswordAsync("kid", AccountTestForms.Password);
        using var browser = host.CreateBrowser();
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(await AccountTestForms.SignInAsync(browser, "kid", AccountTestForms.Password)));
        // Past the stamp interval, so that the stamp check of the held request renews its cookie with the old stamp.
        time.Advance(TimeSpan.FromMinutes(6));
        var held = browser.GetAsync(AuthenticationTestHost.HoldPath);
        await host.Hold.Entered.WaitAsync(TestTimeouts.Default);
        var changed = await AccountTestForms.SetPasswordAsync(browser, "another correct horse", currentPassword: AccountTestForms.Password);
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(changed));

        // Act
        host.Hold.Release();
        using var heldResponse = await held;

        // Assert
        Assert.Equal(HttpStatusCode.OK, heldResponse.StatusCode);
        Assert.False(heldResponse.Headers.TryGetValues("Set-Cookie", out var setCookies) &&
            setCookies.Any(setCookie => setCookie.StartsWith(TestBrowser.SessionCookieName + "=", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/protected")).StatusCode);
    }

    [Fact]
    public async Task WhenTheStoredStampChangedOutsideTheStoreAndTheIntervalPassed_ThenTheStampCheckRefusesTheSessionAndKeepsTheCookie()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await AuthenticationTestHost.StartAsync(time);
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, "kid");
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await userManager.UpdateSecurityStampAsync((await userManager.FindByNameAsync("kid"))!)).Succeeded);
        }

        time.Advance(TimeSpan.FromMinutes(6));

        // Act
        var response = await browser.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(browser.HasSessionCookie);
    }

    [Fact]
    public async Task WhenTheStampCheckRefusesACookiePastHalfItsLifetime_ThenItIsNotRenewedAndLaterRequestsAreRefused()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await AuthenticationTestHost.StartAsync(time);
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, "kid");
        var cookie = browser.SessionCookieValue;
        // The store keeps the old stamp, so only the stamp check against the database refuses the session.
        await host.ChangeSecurityStampAsync("kid");
        time.Advance(TimeSpan.FromDays(8));

        // Act
        var refused = await browser.GetAsync("/protected");
        var later = await browser.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.False(refused.Headers.TryGetValues("Set-Cookie", out var setCookies) &&
            setCookies.Any(setCookie => setCookie.StartsWith(TestBrowser.SessionCookieName + "=", StringComparison.Ordinal)));
        Assert.Equal(cookie, browser.SessionCookieValue);
        Assert.Equal(HttpStatusCode.Redirect, later.StatusCode);
    }

    [Fact]
    public async Task WhenARequestWithARefusedCookieSignsIn_ThenItsNewCookieIsWritten()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, "kid");
        var refusedCookie = browser.SessionCookieValue;
        await host.SetPasswordAsync("kid", AccountTestForms.Password);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/protected")).StatusCode);

        // Act
        await host.SignInAsync(browser, "kid");

        // Assert
        Assert.NotEqual(refusedCookie, browser.SessionCookieValue);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/protected")).StatusCode);
    }

    [Fact]
    public async Task WhenAnUnauthenticatedVisitorRequestsAProtectedEndpoint_ThenTheyAreRedirectedToTheAppWithTheReturnUrl()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var client = host.CreateClient();

        // Act
        var response = await client.GetAsync("/protected?tab=1");

        // Assert
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?ReturnUrl=%2Fprotected%3Ftab%3D1", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task WhenAnUnauthenticatedRequestAcceptsJson_ThenItGets401InsteadOfARedirect()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var client = host.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/protected");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task WhenAVisitorWithoutRolesRequestsAPageOfTheApp_ThenTheGateOfTheAppDecidesInsteadOfTheFallbackPolicy()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var client = host.CreateClient();

        // Act
        var page = await client.GetAsync(BannersTestPage.Path);
        var protectedEndpoint = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, protectedEndpoint.StatusCode);
    }

    [Theory]
    [InlineData("POST", AuthenticationTestHost.HubPath + "/negotiate?negotiateVersion=1")]
    [InlineData("GET", AuthenticationTestHost.HubPath + "/initializers")]
    public async Task WhenAVisitorWithoutRolesOpensTheBlazorHub_ThenItIsReachable(string method, string path)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var client = host.CreateClient();

        // Act
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WhenMoreThanTheLimitOfRequestsThatAreNotSignedInStartTheApp_ThenTheyAreRateLimited()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();
        for (var request = 0; request < UiStartRateLimit.PermitLimit; request++)
        {
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(BannersTestPage.Path)).StatusCode);
        }

        // Act
        var page = await browser.GetAsync(BannersTestPage.Path);
        var hub = await browser.PostAsync(AuthenticationTestHost.HubPath + "/negotiate?negotiateVersion=1", new Dictionary<string, string>());
        var health = await browser.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, page.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, hub.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task WhenASignedInPersonStartsTheAppOftenFromOneAddress_ThenTheyAreNotRateLimited()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var signedOut = host.CreateBrowser();
        for (var request = 0; request < UiStartRateLimit.PermitLimit; request++)
        {
            Assert.Equal(HttpStatusCode.OK, (await signedOut.GetAsync(BannersTestPage.Path)).StatusCode);
        }

        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, AdminSeeding.AdminUserName);

        // Act
        var responses = new List<HttpStatusCode>();
        for (var request = 0; request <= UiStartRateLimit.PermitLimit; request++)
        {
            responses.Add((await browser.GetAsync(BannersTestPage.Path)).StatusCode);
        }

        // Assert
        Assert.All(responses, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await signedOut.GetAsync(BannersTestPage.Path)).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/protected", HttpStatusCode.Forbidden)]
    [InlineData("POST", AuthenticationTestHost.McpPath, HttpStatusCode.Forbidden)]
    [InlineData("POST", AccountEndpoints.SignInPath, HttpStatusCode.Forbidden)]
    [InlineData("GET", BannersTestPage.Path, HttpStatusCode.OK)]
    [InlineData("POST", AuthenticationTestHost.HubPath + "/negotiate?negotiateVersion=1", HttpStatusCode.OK)]
    [InlineData("POST", AccountEndpoints.PasswordPath, HttpStatusCode.BadRequest)]
    [InlineData("POST", AccountEndpoints.LogoutPath, HttpStatusCode.BadRequest)]
    public async Task WhenAPersonMustChangeTheirPassword_ThenOnlyTheAppAndTheAccountEndpointsAnswer(string method, string path, HttpStatusCode expected)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();
        await AccountTestForms.SignInAsync(browser, AdminSeeding.AdminUserName, string.Empty);

        // Act
        var response = method == "GET" ? await browser.GetAsync(path) : await browser.PostAsync(path, new Dictionary<string, string>());

        // Assert
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task WhenAnonymousHasRoles_ThenAnUnauthenticatedVisitorIsAllowed()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(anonymousRoles: [BuiltInRoles.ReaderName]);
        var client = host.CreateClient();

        // Act
        var response = await client.GetAsync("/protected");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task WhenAHealthEndpointIsRequested_ThenNoSignInIsNeeded(string path)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var client = host.CreateClient();

        // Act
        var response = await client.GetAsync(path);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task WhenTheDatabaseIsUnavailable_ThenTheHealthEndpointsStillAnswer(string path)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(databaseUnavailable: true);
        var client = host.CreateClient();

        // Act
        var response = await client.GetAsync(path);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheBlazorHubIsOpenedFromAnotherOrigin_ThenItIsForbidden()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(anonymousRoles: [BuiltInRoles.ReaderName]);
        var client = host.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", "http://localhost:9999");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheBlazorHubIsOpenedFromTheSameOrigin_ThenTheRequestReachesTheHub()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(anonymousRoles: [BuiltInRoles.ReaderName]);
        var client = host.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", "http://localhost");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:9999")]
    [InlineData("https://localhost")]
    [InlineData("http://evil.example")]
    [InlineData("null")]
    public async Task WhenASignedInPersonOpensTheBlazorHubFromAnotherOrigin_ThenItIsForbidden(string origin)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var cookie = await host.SignInAsAdminAsync();
        var client = host.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", origin);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WhenASignedInPersonOpensTheBlazorHubFromTheSameOrigin_ThenTheRequestReachesTheHub()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var cookie = await host.SignInAsAdminAsync();
        var client = host.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", "http://localhost");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenTheDatabaseIsUnavailable_ThenEveryPageShowsAuthenticationUnavailable(bool cannotBeOpened)
    {
        // Arrange
        // A file in place of the folder of the database makes it impossible to create.
        var blockingFile = Path.GetTempFileName();
        try
        {
            await using var host = await AuthenticationTestHost.StartAsync(
                databaseUnavailable: !cannotBeOpened,
                settings: cannotBeOpened ? new Dictionary<string, string> { [nameof(AuthenticationOptions.Database)] = Path.Combine(blockingFile, "authentication.db") } : null);
            var client = host.CreateClient();

            // Act
            var protectedResponse = await client.GetAsync("/protected");
            var appResponse = await client.GetAsync(BannersTestPage.Path);

            // Assert
            Assert.False(host.Database.IsAvailable);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, protectedResponse.StatusCode);
            Assert.True(protectedResponse.Headers.CacheControl?.NoStore);
            var page = await protectedResponse.Content.ReadAsStringAsync();
            Assert.Contains("authentication unavailable", page, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, appResponse.StatusCode);
            if (cannotBeOpened)
            {
                // The reason names no details of the error, such as the path, as visitors who are not signed in see it.
                Assert.Contains("The authentication database cannot be opened or migrated.", page);
                Assert.DoesNotContain(Path.GetFileName(blockingFile), page);
            }
            else
            {
                Assert.Contains("--reset-admin", page);
            }
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public async Task WhenTheCookieIsConfigured_ThenItIsHttpOnlyLaxAndSecureAsTheRequest()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();

        // Act
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);

        // Assert
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, options.Cookie.SecurePolicy);
        Assert.Equal(TimeSpan.FromDays(14), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.Equal("/", options.LoginPath);
        Assert.Equal("/", options.AccessDeniedPath);
    }

    [Fact]
    public async Task WhenTheMcpUserHasNoKey_ThenTheMcpEndpointNeedsNone()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var client = host.CreateClient();

        // Act
        var response = await client.PostAsync(AuthenticationTestHost.McpPath, content: null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task WhenTheMcpUserHasAKey_ThenARequestWithoutItIsRefused(string? presentedKey)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var created = await host.AdministrationAsAdmin.CreateMcpKeyAsync(CancellationToken.None);
        Assert.True(created.Succeeded, created.Error);
        var client = host.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, AuthenticationTestHost.McpPath);
        if (presentedKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", presentedKey);
        }

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task WhenTheMcpUserHasAKey_ThenARequestWithItPasses()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var created = await host.AdministrationAsAdmin.CreateMcpKeyAsync(CancellationToken.None);
        Assert.True(created.Succeeded, created.Error);
        var client = host.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, AuthenticationTestHost.McpPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created.McpKey);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:9999")]
    [InlineData("https://localhost")]
    [InlineData("http://evil.example")]
    [InlineData("null")]
    public async Task WhenTheMcpEndpointIsCalledFromAnotherOrigin_ThenItIsForbidden(string origin)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var client = host.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, AuthenticationTestHost.McpPath);
        request.Headers.Add("Origin", origin);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheMcpEndpointIsCalledFromAnotherOriginWithTheKey_ThenItIsForbidden()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var created = await host.AdministrationAsAdmin.CreateMcpKeyAsync(CancellationToken.None);
        Assert.True(created.Succeeded, created.Error);
        var client = host.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, AuthenticationTestHost.McpPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created.McpKey);
        request.Headers.Add("Origin", "http://evil.example");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheMcpEndpointIsCalledFromTheSameOrigin_ThenItPasses()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var client = host.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, AuthenticationTestHost.McpPath);
        request.Headers.Add("Origin", "http://localhost");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Directly in the database, which the stamp check of a fresh cookie does not read, and then in the user store; returns the stamp before.
    private static async Task<string> SetStoredSecurityStampAsync(AuthenticationTestHost host, string userName, string securityStamp)
    {
        string previous;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await userManager.FindByNameAsync(userName))!;
            previous = user.SecurityStamp!;
            user.SecurityStamp = securityStamp;
            Assert.True((await userManager.UpdateAsync(user)).Succeeded);
        }

        await host.UserStore.RefreshUserAsync(host.UserStore.GetUserId(userName)!, CancellationToken.None);
        return previous;
    }

    // A session cookie of the user named userName, as signing in issues it now, with the stamp stored now; returns it as a Cookie header value with that stamp.
    private static async Task<(string Cookie, string SecurityStamp)> CreateSessionCookieAsync(AuthenticationTestHost host, string userName)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await userManager.FindByNameAsync(userName))!;
        var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(user);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var now = DateTimeOffset.UtcNow;
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now.Add(options.ExpireTimeSpan) }, IdentityConstants.ApplicationScheme);
        return ($"{TestBrowser.SessionCookieName}={options.TicketDataFormat.Protect(ticket)}", user.SecurityStamp!);
    }
}
