using HomeBlaze.Authentication;
using HomeBlaze.Authorization;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for signing in and out and managing the account in the dialogs of the app, on a server of their own whose
/// seeded administrator still has the empty password of seeding.
/// </summary>
[Collection(nameof(UnclaimedAdministratorCollection))]
[Trait("Category", "Integration")]
public class SignInTests
{
    private const string NewPassword = "e2e-new-password";
    private const string InvalidSignIn = "The user name or password is wrong, or the account is locked. Try again later.";

    private readonly UnclaimedAdministratorFixture _fixture;

    public SignInTests(UnclaimedAdministratorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAVisitorWithoutRolesOpensAPage_ThenOnlyTheFrameAndTheSignInDialogShowAndSigningInStaysOnThePage()
    {
        // Arrange
        var userName = TestNames.Create("visitor");
        await _fixture.AddPersonAsync(userName, BuiltInRoles.ReaderName);
        try
        {
            var page = await _fixture.CreatePageAsync();

            // Act
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Controls.md");

            // Assert
            await Assertions.Expect(page.Locator("#sign-in-user-name")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("app-bar-brand")).ToHaveTextAsync("HomeBlaze");
            await Assertions.Expect(page.Locator("#nav-drawer")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
            await Assertions.Expect(page.GetByTestId("user-menu-sign-in")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
            await Assertions.Expect(page.GetByTestId("unencrypted-http-warning")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".mud-dialog .mud-button-close")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
            Assert.Equal("/pages/Demo/Controls.md", new Uri(page.Url).AbsolutePath);

            // Act
            await PlaywrightFixture.FillSignInDialogAsync(page, userName, PlaywrightFixture.Password);

            // Assert
            await AssertSignedInAsync(page, userName);
            Assert.Equal("/pages/Demo/Controls.md", new Uri(page.Url).AbsolutePath);
        }
        finally
        {
            await _fixture.RemovePersonAsync(userName);
        }
    }

    [Fact]
    public async Task WhenAWrongPasswordIsEntered_ThenTheDialogShowsTheRefusalAndClearsThePassword()
    {
        // Arrange
        var userName = TestNames.Create("mistyped");
        await _fixture.AddPersonAsync(userName, BuiltInRoles.ReaderName);
        try
        {
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync(_fixture.ServerAddress);

            // Act
            await PlaywrightFixture.FillSignInDialogAsync(page, userName, "a wrong password");

            // Assert
            await Assertions.Expect(page.GetByTestId("account-form-errors")).ToHaveTextAsync(InvalidSignIn);
            await Assertions.Expect(page.Locator("#sign-in-password")).ToHaveValueAsync(string.Empty);
            await Assertions.Expect(page.Locator("#sign-in-user-name")).ToHaveValueAsync(userName);
            await Assertions.Expect(page.GetByTestId("user-menu")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
        }
        finally
        {
            await _fixture.RemovePersonAsync(userName);
        }
    }

    [Fact]
    public async Task WhenAnonymousHoldsReader_ThenTheAppShowsAndTheSignInDialogOfTheUserMenuSignsInOnTheSamePage()
    {
        // Arrange
        var userName = TestNames.Create("household");
        await _fixture.AddPersonAsync(userName, BuiltInRoles.ReaderName);
        await SetAnonymousRolesAsync([BuiltInRoles.ReaderName]);
        try
        {
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Controls.md");
            await page.WaitUntilInteractiveAsync();
            await Assertions.Expect(page.Locator("#nav-drawer")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".mud-dialog")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });

            // Act
            await PlaywrightFixture.FillSignInDialogAsync(page, userName, PlaywrightFixture.Password);

            // Assert
            await AssertSignedInAsync(page, userName);
            Assert.Equal("/pages/Demo/Controls.md", new Uri(page.Url).AbsolutePath);
        }
        finally
        {
            await SetAnonymousRolesAsync([]);
            await _fixture.RemovePersonAsync(userName);
        }
    }

    [Fact]
    public async Task WhenTheSeededAdministratorSignsInWithAnEmptyPassword_ThenTheySetAPasswordInADialogThatCannotBeClosed()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        try
        {
            // Act
            await _fixture.SignInAsync(page, AdminSeeding.AdminUserName, string.Empty);

            // Assert
            var dialog = page.GetByTestId("set-password-dialog");
            await Assertions.Expect(dialog).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#set-password-current")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
            await Assertions.Expect(page.Locator(".mud-dialog .mud-button-close")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
            await Assertions.Expect(page.Locator("#nav-drawer")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
            await page.GetByRole(AriaRole.Dialog).WaitForDialogFocusAsync();
            await page.Locator("#set-password-new").FocusAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(dialog).ToBeVisibleAsync();

            // Act
            await SetPasswordAsync(page, "set-password");

            // Assert
            await AssertSignedInAsync(page, AdminSeeding.AdminUserName);
            Assert.False(_fixture.ServerServices.GetRequiredService<UserStore>().IsSeededAdministratorUnclaimed);
        }
        finally
        {
            await _fixture.RestoreUnclaimedAdministratorAsync();
        }
    }

    [Fact]
    public async Task WhenASetupCodeIsCreatedWithTheAddUserOperation_ThenTheSetupDialogSetsThePasswordAndSignsTheNewUserIn()
    {
        // Arrange
        var managerName = TestNames.Create("manager");
        var addedName = TestNames.Create("added");
        await _fixture.AddPersonAsync(managerName, BuiltInRoles.EverythingName);
        try
        {
            var page = await _fixture.CreatePageAsync(SignedInAs.User(managerName));
            await page.GotoAsync($"{_fixture.ServerAddress}browser/Authorization");
            await page.WaitForFunctionAsync(SubjectBrowserPage.IsInteractive, null, new() { Timeout = AssertionTimeouts.Default });
            await Assertions.Expect(page.Locator("#scrollContainer [data-testid='subject-pane']")).ToHaveCountAsync(2);
            var dialog = await SubjectBrowserPage.OpenOperationDialogAsync(page.Locator("#scrollContainer [data-testid='subject-pane']").Last, "Add User");
            await dialog.Locator("input").First.FillAsync(addedName);
            await Assertions.Expect(dialog.Locator("textarea")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
            await SubjectBrowserPage.ExecuteAsync(dialog);
            var secret = page.GetByTestId("one-time-secret-value");
            await Assertions.Expect(secret).Not.ToBeEmptyAsync();
            var setupCode = (await secret.InnerTextAsync()).Trim();
            await Assertions.Expect(page.GetByTestId("one-time-secret-link")).ToHaveTextAsync($"{_fixture.ServerAddress}account/setup");

            // Act
            var setupPage = await _fixture.CreatePageAsync();
            var response = await setupPage.GotoAsync($"{_fixture.ServerAddress}account/setup");
            await setupPage.Locator("#setup-user-name").FillAsync(addedName);
            await setupPage.Locator("#setup-code").FillAsync(setupCode);
            await SetPasswordAsync(setupPage, "setup");

            // Assert
            Assert.Contains("no-store", response!.Headers["cache-control"], StringComparison.Ordinal);
            Assert.Equal("no-referrer", response.Headers["referrer-policy"]);
            await AssertSignedInAsync(setupPage, addedName);
            var signInPage = await _fixture.CreatePageAsync();
            await _fixture.SignInAsync(signInPage, addedName, NewPassword);
            await AssertSignedInAsync(signInPage, addedName);
        }
        finally
        {
            var userStore = _fixture.ServerServices.GetRequiredService<UserStore>();
            if (userStore.GetUserId(addedName) is not null)
            {
                await _fixture.RemovePersonAsync(addedName);
            }

            await _fixture.RemovePersonAsync(managerName);
        }
    }

    [Fact]
    public async Task WhenAPersonChangesTheirPasswordInTheAccountDialog_ThenOnlyTheNewPasswordSignsThemIn()
    {
        // Arrange
        var userName = TestNames.Create("changing");
        await _fixture.AddPersonAsync(userName, BuiltInRoles.ReaderName);
        try
        {
            var page = await _fixture.CreatePageAsync(SignedInAs.User(userName));
            await page.WaitUntilInteractiveAsync();
            await page.GetByTestId("user-menu").ClickAsync();
            await page.GetByTestId("user-menu-account").ClickAsync();
            var dialog = page.GetByTestId("account-dialog");
            await Assertions.Expect(dialog.GetByTestId("account-name")).ToHaveTextAsync(userName);
            await Assertions.Expect(dialog.GetByTestId("account-roles")).ToHaveTextAsync(BuiltInRoles.ReaderName);
            await Assertions.Expect(page.GetByTestId("unencrypted-http-warning")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });

            // Act
            await page.Locator("#account-password-current").FillAsync(PlaywrightFixture.Password);
            await SetPasswordAsync(page, "account-password");

            // Assert
            await Assertions.Expect(dialog).ToHaveCountAsync(0);
            await AssertSignedInAsync(page, userName);

            var oldPasswordPage = await _fixture.CreatePageAsync();
            await oldPasswordPage.GotoAsync(_fixture.ServerAddress);
            await PlaywrightFixture.FillSignInDialogAsync(oldPasswordPage, userName, PlaywrightFixture.Password);
            await Assertions.Expect(oldPasswordPage.GetByTestId("account-form-errors")).ToHaveTextAsync(InvalidSignIn);

            var newPasswordPage = await _fixture.CreatePageAsync();
            await _fixture.SignInAsync(newPasswordPage, userName, NewPassword);
            await AssertSignedInAsync(newPasswordPage, userName);
        }
        finally
        {
            await _fixture.RemovePersonAsync(userName);
        }
    }

    [Fact]
    public async Task WhenAPersonSignsOutFromTheUserMenu_ThenTheyAreSignedOutAndSeeTheSignInDialog()
    {
        // Arrange
        var userName = TestNames.Create("leaving");
        await _fixture.AddPersonAsync(userName, BuiltInRoles.ReaderName);
        try
        {
            var page = await _fixture.CreatePageAsync(SignedInAs.User(userName));
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Controls.md");
            await page.WaitUntilInteractiveAsync();

            // Act: the form in the menu posts the antiforgery token of the prerendered page.
            await page.GetByTestId("user-menu").ClickAsync();
            await page.Locator(".mud-popover-open").GetByRole(AriaRole.Menuitem, new() { Name = "Sign out" }).ClickAsync();

            // Assert
            await _fixture.WaitForUrlAsync(page, url => new Uri(url).AbsolutePath == "/");
            await Assertions.Expect(page.Locator("#sign-in-user-name")).ToBeVisibleAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Controls.md");
            await Assertions.Expect(page.Locator("#sign-in-user-name")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("user-menu")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
        }
        finally
        {
            await _fixture.RemovePersonAsync(userName);
        }
    }

    private async Task SetAnonymousRolesAsync(IReadOnlyCollection<string> roles)
    {
        var result = await _fixture.CreateAdministration().SetRolesAsync(NameRules.AnonymousUserName, roles, CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
    }

    // Fills the new password of the form whose inputs start with idPrefix and submits it.
    private static async Task SetPasswordAsync(IPage page, string idPrefix)
    {
        await page.Locator($"#{idPrefix}-new").FillAsync(NewPassword);
        await page.Locator($"#{idPrefix}-confirm").FillAsync(NewPassword);
        await page.Locator($"#{idPrefix}-confirm").Locator("xpath=ancestor::form").GetByTestId("account-form-submit").ClickAsync();
    }

    // Loaded anew and interactive, with the person named in the user menu and no account dialog left.
    private static async Task AssertSignedInAsync(IPage page, string userName)
    {
        await Assertions.Expect(page.GetByTestId("user-menu")).ToBeVisibleAsync();
        await page.WaitUntilInteractiveAsync();
        await Assertions.Expect(page.Locator(".mud-dialog")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
        await page.GetByTestId("user-menu").ClickAsync();
        await Assertions.Expect(page.GetByTestId("user-menu-name")).ToHaveTextAsync(userName);
        await page.Keyboard.PressAsync("Escape");
    }
}
