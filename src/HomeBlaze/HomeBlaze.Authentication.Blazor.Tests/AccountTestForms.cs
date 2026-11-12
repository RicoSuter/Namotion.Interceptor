using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>The posts of the account dialogs in the tests of the account endpoints.</summary>
internal static class AccountTestForms
{
    public const string Password = "correct horse battery";

    public const string InvalidSignIn = "The user name or password is wrong, or the account is locked. Try again later.";

    public static Task<HttpResponseMessage> SignInAsync(TestBrowser browser, string userName, string password, string? returnUrl = null) =>
        browser.PostAccountFormAsync(AccountEndpoints.SignInPath, new Dictionary<string, string>
        {
            ["userName"] = userName,
            ["password"] = password,
            ["returnUrl"] = returnUrl ?? string.Empty
        });

    public static Task<HttpResponseMessage> SetPasswordAsync(TestBrowser browser, string newPassword, string? confirmPassword = null, string currentPassword = "") =>
        browser.PostAccountFormAsync(AccountEndpoints.PasswordPath, new Dictionary<string, string>
        {
            ["currentPassword"] = currentPassword,
            ["newPassword"] = newPassword,
            ["confirmPassword"] = confirmPassword ?? newPassword
        });

    public static Task<HttpResponseMessage> RedeemSetupCodeAsync(TestBrowser browser, string userName, string code, string newPassword = Password, string? confirmPassword = null) =>
        browser.PostAccountFormAsync(AccountEndpoints.SetupPath, new Dictionary<string, string>
        {
            ["userName"] = userName,
            ["code"] = code,
            ["newPassword"] = newPassword,
            ["confirmPassword"] = confirmPassword ?? newPassword
        });

    public static Task<HttpResponseMessage> SetDisplayNameAsync(TestBrowser browser, string displayName, string? returnUrl = null, bool withToken = true) =>
        browser.PostAccountFormAsync(AccountEndpoints.DisplayNamePath, new Dictionary<string, string>
        {
            ["displayName"] = displayName,
            ["returnUrl"] = returnUrl ?? string.Empty
        }, withToken);

    public static async Task<bool> HasPasswordAsync(AuthenticationTestHost host, string userName)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await userManager.HasPasswordAsync((await userManager.FindByNameAsync(userName))!);
    }
}
