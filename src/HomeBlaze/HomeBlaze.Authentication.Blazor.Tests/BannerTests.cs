using HomeBlaze.Authorization;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class BannerTests
{
    private const string UnclaimedAdministratorBanner = "The administrator has no password yet";
    private const string Password = "correct horse battery";

    [Fact]
    public async Task WhenASignedInPersonViewsAPageOverHttp_ThenNoUnencryptedBannerShows()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("mom", null, [BuiltInRoles.EverythingName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, "mom");

        // Act
        var page = await GetPageAsync(browser);

        // Assert
        Assert.DoesNotContain("unencrypted HTTP", page);
        Assert.Contains(UnclaimedAdministratorBanner, page);
    }

    [Fact]
    public async Task WhenTheSeededAdminHasNoPassword_ThenAFullAdministratorSeesTheUnclaimedBanner()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("mom", null, [BuiltInRoles.EverythingName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, "mom");

        // Act
        var page = await GetPageAsync(browser);

        // Assert
        Assert.Contains(UnclaimedAdministratorBanner, page);
    }

    [Fact]
    public async Task WhenTheSeededAdminSetAPassword_ThenTheUnclaimedBannerIsHidden()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await host.SetPasswordAsync(AdminSeeding.AdminUserName, Password);
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, AdminSeeding.AdminUserName);

        // Act
        var page = await GetPageAsync(browser);

        // Assert
        Assert.DoesNotContain(UnclaimedAdministratorBanner, page);
    }

    [Fact]
    public async Task WhenAPersonIsNoFullAdministrator_ThenTheUnclaimedBannerIsHidden()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, [BuiltInRoles.ReaderName], CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        using var browser = host.CreateBrowser();
        await host.SignInAsync(browser, "kid");

        // Act
        var page = await GetPageAsync(browser);

        // Assert
        Assert.DoesNotContain(UnclaimedAdministratorBanner, page);
    }

    [Fact]
    public async Task WhenAVisitorIsNotSignedIn_ThenNoBannerShows()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(anonymousRoles: [BuiltInRoles.EverythingName]);
        using var browser = host.CreateBrowser();

        // Act
        var page = await GetPageAsync(browser);

        // Assert
        Assert.DoesNotContain(UnclaimedAdministratorBanner, page);
    }

    private static async Task<string> GetPageAsync(TestBrowser browser)
    {
        using var response = await browser.GetAsync(BannersTestPage.Path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
