using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for authentication/login functionality.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
public class LoginTests
{
    private readonly PlaywrightFixture _fixture;

    public LoginTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LoginPage_ShouldDisplayLoginForm()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await page.GotoAsync($"{_fixture.ServerAddress}login");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert - login form elements should be visible
        var usernameField = page.GetByLabel("Username");
        await Assertions.Expect(usernameField).ToBeVisibleAsync(new() { Timeout = 30000 });

        var passwordField = page.GetByLabel("Password");
        await Assertions.Expect(passwordField).ToBeVisibleAsync(new() { Timeout = 30000 });

        var signInButton = page.GetByRole(AriaRole.Button, new() { Name = "Sign In" });
        await Assertions.Expect(signInButton).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task LoginPage_ShouldHaveRememberMeCheckbox()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await page.GotoAsync($"{_fixture.ServerAddress}login");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert - remember me checkbox should be visible
        var rememberMe = page.GetByLabel("Remember me");
        await Assertions.Expect(rememberMe).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task LoginPage_ShouldHaveLoginHeader()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await page.GotoAsync($"{_fixture.ServerAddress}login");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert - login header should be visible
        var header = page.GetByRole(AriaRole.Heading, new() { Name = "Login" });
        await Assertions.Expect(header).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task LoginPage_WithInvalidCredentials_ShouldShowError()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}login");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Act - fill in invalid credentials
        await page.GetByLabel("Username").FillAsync("invaliduser");
        await page.GetByLabel("Password").FillAsync("wrongpassword");

        // Submit the form
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign In" }).ClickAsync();

        // Wait for navigation and error to appear
        await page.WaitForURLAsync(url => url.Contains("/login"), new() { Timeout = 30000 });

        // Assert - error message should be visible (MudAlert with Severity.Error)
        var alert = page.Locator(".mud-alert-text-error");
        await Assertions.Expect(alert).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task LoginPage_ShouldAcceptReturnUrlParameter()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        var returnUrl = "/browser";

        // Act
        await page.GotoAsync($"{_fixture.ServerAddress}login?returnUrl={returnUrl}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert - the form should contain the hidden return URL field
        var hiddenReturnUrl = page.Locator("input[name='ReturnUrl']");
        await Assertions.Expect(hiddenReturnUrl).ToHaveValueAsync(returnUrl, new() { Timeout = 30000 });
    }

    [Fact]
    public async Task LoginPage_ShouldAcceptPathBasedReturnUrl()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act - use path-based return URL
        await page.GotoAsync($"{_fixture.ServerAddress}login/%2Fbrowser");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert - the form should be visible (page should load correctly)
        var signInButton = page.GetByRole(AriaRole.Button, new() { Name = "Sign In" });
        await Assertions.Expect(signInButton).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task LoginPage_UsernameField_ShouldBeEditable()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}login");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Act
        var usernameField = page.GetByLabel("Username");
        await usernameField.FillAsync("testuser");

        // Assert
        await Assertions.Expect(usernameField).ToHaveValueAsync("testuser", new() { Timeout = 30000 });
    }

    [Fact]
    public async Task LoginPage_PasswordField_ShouldBeEditable()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}login");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Act
        var passwordField = page.GetByLabel("Password");
        await passwordField.FillAsync("testpassword");

        // Assert
        await Assertions.Expect(passwordField).ToHaveValueAsync("testpassword", new() { Timeout = 30000 });
    }
}
