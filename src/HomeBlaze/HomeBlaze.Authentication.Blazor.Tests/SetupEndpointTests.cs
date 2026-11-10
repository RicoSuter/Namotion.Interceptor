using System.Net;
using HomeBlaze.Authorization;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor.Testing;
using Xunit;
using static HomeBlaze.Authentication.Blazor.Tests.AccountTestForms;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class SetupEndpointTests
{
    [Fact]
    public async Task WhenASetupCodeIsUsed_ThenTheUserIsSignedInWithTheirNewPasswordAndTheCodeIsSpent()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var host = await AuthenticationTestHost.StartAsync(logger: logger);
        var added = await host.AdministrationAsAdmin.AddUserAsync("mom", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();

        // Act
        var response = await RedeemSetupCodeAsync(browser, "mom", added.SetupCode!);

        // Assert
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(response));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/protected")).StatusCode);
        Assert.False(host.GetUser("mom").MustChangePassword);
        Assert.NotNull(host.GetUser("mom").LastLogin);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(added.SetupCode!, StringComparison.OrdinalIgnoreCase));

        using var signInBrowser = host.CreateBrowser();
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(await SignInAsync(signInBrowser, "mom", Password)));

        using var reuseBrowser = host.CreateBrowser();
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(await RedeemSetupCodeAsync(reuseBrowser, "mom", added.SetupCode!, "a password of the attacker")));
        Assert.False(reuseBrowser.HasSessionCookie);
    }

    [Fact]
    public async Task WhenTheLastSaveOfThePasswordFails_ThenNothingIsStoredAndThePersonIsSignedInToSetIt()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("mom", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();
        host.FailingUserValidator.IsRefusing = true;
        var redeemed = await RedeemSetupCodeAsync(browser, "mom", added.SetupCode!);
        var hadPassword = await HasPasswordAsync(host, "mom");
        host.FailingUserValidator.IsRefusing = false;

        // Act
        var response = await SetPasswordAsync(browser, Password);

        // Assert
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(redeemed));
        Assert.False(hadPassword);
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(response));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/protected")).StatusCode);
    }

    [Fact]
    public async Task WhenTheUserNameIsTypedWithUpperCaseLetters_ThenTheSetupCodeOfTheLowerCaseUserIsRedeemed()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("mom", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();

        // Act
        var response = await RedeemSetupCodeAsync(browser, "Mom", added.SetupCode!);

        // Assert
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(response));
        Assert.False(host.GetUser("mom").MustChangePassword);
    }

    [Fact]
    public async Task WhenTheUserNameIsTypedWithALetterOutsideAscii_ThenTheSetupCodeIsRefusedAndStaysValid()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("kim", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();

        // Act
        var kelvinSign = await RedeemSetupCodeAsync(browser, "\u212Aim", added.SetupCode!);

        // Assert
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(kelvinSign));
        Assert.False(browser.HasSessionCookie);
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(await RedeemSetupCodeAsync(browser, "kim", added.SetupCode!)));
    }

    [Fact]
    public async Task WhenTheCodeIsWrongOrTheUserUnknown_ThenBothAreRefusedWithTheSameMessage()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("mom", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();

        // Act
        var wrongCode = await RedeemSetupCodeAsync(browser, "mom", "00000000000000000000");
        var unknownUser = await RedeemSetupCodeAsync(browser, "nobody", added.SetupCode!);
        var builtInUser = await RedeemSetupCodeAsync(browser, NameRules.AnonymousUserName, added.SetupCode!);

        // Assert
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(wrongCode));
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(unknownUser));
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(builtInUser));
        Assert.False(browser.HasSessionCookie);
        Assert.False(await HasPasswordAsync(host, "mom"));
    }

    [Fact]
    public async Task WhenTheCodeExpired_ThenItIsRefusedWithTheSameMessage()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await AuthenticationTestHost.StartAsync(time);
        var added = await host.AdministrationAsAdmin.AddUserAsync("mom", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        time.Advance(SetupCodes.Lifetime + TimeSpan.FromMinutes(1));
        using var browser = host.CreateBrowser();

        // Act
        var response = await RedeemSetupCodeAsync(browser, "mom", added.SetupCode!);

        // Assert
        Assert.Equal([InvalidSignIn], await TestBrowser.GetAccountErrorsAsync(response));
        Assert.False(browser.HasSessionCookie);
        Assert.False(await HasPasswordAsync(host, "mom"));
    }

    [Theory]
    [InlineData("correct horse battery", "something else", "The new passwords do not match.")]
    [InlineData("short", "short", "10")]
    public async Task WhenTheNewPasswordIsRefused_ThenTheCodeIsNotSpent(string newPassword, string confirmPassword, string expectedError)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("mom", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();

        // Act
        var refused = await RedeemSetupCodeAsync(browser, "mom", added.SetupCode!, newPassword, confirmPassword);

        // Assert
        Assert.Contains(expectedError, Assert.Single(await TestBrowser.GetAccountErrorsAsync(refused)), StringComparison.Ordinal);
        Assert.False(browser.HasSessionCookie);
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(await RedeemSetupCodeAsync(browser, "mom", added.SetupCode!)));
    }

    [Fact]
    public async Task WhenTheCodeIsPostedWithoutAnAntiforgeryToken_ThenItIsRefusedAndNotSpent()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("mom", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();

        // Act
        var response = await browser.PostAccountFormAsync(AccountEndpoints.SetupPath, new Dictionary<string, string>
        {
            ["userName"] = "mom",
            ["code"] = added.SetupCode!,
            ["newPassword"] = Password,
            ["confirmPassword"] = Password
        }, withToken: false);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(browser.HasSessionCookie);
        Assert.Equal("/", await TestBrowser.GetAccountRedirectAsync(await RedeemSetupCodeAsync(browser, "mom", added.SetupCode!)));
    }

    [Fact]
    public async Task WhenTheSetupPageIsRequestedWithACode_ThenItNeedsNoSignIn()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        using var browser = host.CreateBrowser();

        // Act
        var response = await browser.GetAsync("/account/setup?user=mom&code=ABCD1234");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
