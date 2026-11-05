using Bunit;
using HomeBlaze.Authentication.Blazor.Components;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public sealed class AccountDialogTests : AccountComponentsTestContext
{
    private const string ModulePath = "./_content/HomeBlaze.Authentication.Blazor/Components/AccountForm.razor.js";

    [Fact]
    public async Task WhenASignedInPersonOpensTheAccountDialog_ThenItListsTheirRolesAndChangesThePasswordWithoutBindingIt()
    {
        // Arrange
        await UseGraphAsync(roles: [BuiltInRoles.ReaderName, BuiltInRoles.OperatorName]);
        var provider = RenderDialogProvider();

        // Act
        await provider.InvokeAsync(() => AccountDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), "/pages/a"));

        // Assert
        await provider.WaitForAssertionAsync(() => Assert.NotEmpty(provider.FindAll("[data-testid=account-password-form]")));
        Assert.Equal(PermissionsTestGraph.TestUserName, provider.Find("[data-testid=account-name]").TextContent);
        Assert.Equal([BuiltInRoles.OperatorName, BuiltInRoles.ReaderName], provider.FindAll("[data-testid=account-roles] li").Select(item => item.TextContent));
        Assert.Empty(provider.FindAll("[data-testid=account-no-roles]"));
        var form = provider.Find("form[data-testid=account-password-form]");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("account/password", form.GetAttribute("action"));
        Assert.Equal(TokenValue, form.QuerySelector($"input[type=hidden][name={TokenFieldName}]")?.GetAttribute("value"));
        Assert.Equal("/pages/a", form.QuerySelector("input[name=returnUrl]")?.GetAttribute("value"));
        Assert.Equal("new-password", form.QuerySelector("input[name=newPassword]")?.GetAttribute("autocomplete"));
        Assert.Null(form.QuerySelector("input[name=newPassword]")?.GetAttribute("value"));
        // Without a password there is no current one to enter.
        Assert.Null(form.QuerySelector("input[name=currentPassword]"));
        Assert.Equal("account/logout", provider.Find("form[data-testid=account-sign-out]").GetAttribute("action"));
        Assert.NotEmpty(provider.FindAll(".mud-dialog .mud-button-close"));
    }

    [Fact]
    public async Task WhenThePersonHasADisplayName_ThenTheDialogNamesBothNamesAndPrefillsTheDisplayNameForm()
    {
        // Arrange
        var graph = await UseGraphAsync(roles: [BuiltInRoles.ReaderName]);
        var result = await graph.UserStore.SetOwnDisplayNameAsync(graph.UserStore.GetUserId(PermissionsTestGraph.TestUserName)!, "Rico M\u00fcller", CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
        var provider = RenderDialogProvider();

        // Act
        await provider.InvokeAsync(() => AccountDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), "/pages/a"));

        // Assert
        await provider.WaitForAssertionAsync(() => Assert.NotEmpty(provider.FindAll("[data-testid=account-display-name-form]")));
        Assert.Equal("Rico M\u00fcller", provider.Find("[data-testid=account-display-name]").TextContent);
        Assert.Equal(PermissionsTestGraph.TestUserName, provider.Find("[data-testid=account-name]").TextContent);
        var form = provider.Find("form[data-testid=account-display-name-form]");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("account/display-name", form.GetAttribute("action"));
        Assert.Equal(TokenValue, form.QuerySelector($"input[type=hidden][name={TokenFieldName}]")?.GetAttribute("value"));
        Assert.Equal("/pages/a", form.QuerySelector("input[name=returnUrl]")?.GetAttribute("value"));
        Assert.Equal("Rico M\u00fcller", form.QuerySelector("input[name=displayName]")?.GetAttribute("value"));
        Assert.Equal("100", form.QuerySelector("input[name=displayName]")?.GetAttribute("maxlength"));
    }

    [Fact]
    public async Task WhenThePersonHasNoDisplayName_ThenTheDialogNamesTheUserNameOnlyAndTheFormIsEmpty()
    {
        // Arrange
        await UseGraphAsync(roles: [BuiltInRoles.ReaderName]);
        var provider = RenderDialogProvider();

        // Act
        await provider.InvokeAsync(() => AccountDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), "/"));

        // Assert
        await provider.WaitForAssertionAsync(() => Assert.NotEmpty(provider.FindAll("[data-testid=account-display-name-form]")));
        Assert.Empty(provider.FindAll("[data-testid=account-display-name]"));
        Assert.Equal(PermissionsTestGraph.TestUserName, provider.Find("[data-testid=account-name]").TextContent);
        Assert.True(string.IsNullOrEmpty(provider.Find("input[name=displayName]").GetAttribute("value")));
    }

    [Fact]
    public async Task WhenThePersonHasAPassword_ThenTheAccountDialogAsksForTheCurrentOne()
    {
        // Arrange
        await UseGraphAsync(roles: [BuiltInRoles.ReaderName]);
        await using (var scope = Graph.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await userManager.FindByNameAsync(PermissionsTestGraph.TestUserName))!;
            Assert.True((await userManager.AddPasswordAsync(user, AccountTestForms.Password)).Succeeded);
        }

        var provider = RenderDialogProvider();

        // Act
        await provider.InvokeAsync(() => AccountDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), "/"));

        // Assert
        await provider.WaitForAssertionAsync(() => Assert.NotEmpty(provider.FindAll("[data-testid=account-password-form]")));
        Assert.Equal("current-password", provider.Find("input[name=currentPassword]").GetAttribute("autocomplete"));
    }

    [Fact]
    public async Task WhenAPersonWithoutRolesOpensTheAccountDialog_ThenItSaysToAskAnAdministrator()
    {
        // Arrange
        await UseGraphAsync(roles: []);
        var provider = RenderDialogProvider();

        // Act
        await provider.InvokeAsync(() => AccountDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), "/"));

        // Assert
        await provider.WaitForAssertionAsync(() => Assert.Equal("No roles yet. Ask an administrator.", provider.Find("[data-testid=account-no-roles]").TextContent));
    }

    [Fact]
    public async Task WhenTheSignInDialogIsShown_ThenItHasAPlainFormWithTheTokenAndWarnsOfUnencryptedHttp()
    {
        // Arrange
        await UseGraphAsync(roles: null);
        var provider = RenderDialogProvider();

        // Act
        await provider.InvokeAsync(() => SignInDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), "/pages/a", isClosable: true));

        // Assert
        await provider.WaitForAssertionAsync(() => Assert.NotEmpty(provider.FindAll("[data-testid=sign-in-form]")));
        var form = provider.Find("form[data-testid=sign-in-form]");
        Assert.Equal("account/sign-in", form.GetAttribute("action"));
        Assert.Equal(TokenValue, form.QuerySelector($"input[type=hidden][name={TokenFieldName}]")?.GetAttribute("value"));
        Assert.Equal("username", form.QuerySelector("input[name=userName]")?.GetAttribute("autocomplete"));
        Assert.Equal("current-password", form.QuerySelector("input[name=password]")?.GetAttribute("autocomplete"));
        Assert.Equal("true", form.QuerySelector("input[name=rememberMe]")?.GetAttribute("value"));
        Assert.Equal("/pages/a", form.QuerySelector("input[name=returnUrl]")?.GetAttribute("value"));
        Assert.NotEmpty(provider.FindAll("[data-testid=unencrypted-http-warning]"));
        Assert.NotEmpty(provider.FindAll(".mud-dialog .mud-button-close"));
    }

    [Fact]
    public async Task WhenTheScriptCannotBeAttached_ThenTheFormCannotBeSubmitted()
    {
        // Arrange
        await UseGraphAsync(roles: null);
        JSInterop.SetupModule(ModulePath).SetupVoid("register", _ => true).SetException(new Microsoft.JSInterop.JSException("The script failed."));
        var provider = RenderDialogProvider();

        // Act
        await provider.InvokeAsync(() => SignInDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), "/", isClosable: true));

        // Assert
        await provider.WaitForAssertionAsync(() => Assert.True(provider.Find("[data-testid=account-form-submit]").HasAttribute("disabled")));
    }

    [Fact]
    public async Task WhenTheScriptIsAttachedAndAPostIsRefused_ThenTheFormIsEnabledAndShowsTheMessages()
    {
        // Arrange
        await UseGraphAsync(roles: null);
        var module = JSInterop.SetupModule(ModulePath);
        module.SetupVoid("register", _ => true).SetVoidResult();
        var provider = RenderDialogProvider();
        await provider.InvokeAsync(() => SignInDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), "/", isClosable: true));
        await provider.WaitForAssertionAsync(() => Assert.False(provider.Find("[data-testid=account-form-submit]").HasAttribute("disabled")));

        // Act
        var form = provider.FindComponent<AccountForm>();
        await form.InvokeAsync(() => form.Instance.OnRefused([AccountTestForms.InvalidSignIn]));

        // Assert
        Assert.Equal(AccountTestForms.InvalidSignIn, provider.Find("[data-testid=account-form-errors]").TextContent.Trim());
        Assert.Single(module.Invocations, invocation => invocation.Identifier == "register");
    }

    [Fact]
    public async Task WhenTheSetupDialogIsShown_ThenItTakesTheCodeAndTheNewPasswordAndWarnsOfUnencryptedHttp()
    {
        // Arrange
        await UseGraphAsync(roles: null);
        var provider = RenderDialogProvider();

        // Act
        await provider.InvokeAsync(() => SetupDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), "mom", "ABCD1234", isClosable: true));

        // Assert
        await provider.WaitForAssertionAsync(() => Assert.NotEmpty(provider.FindAll("[data-testid=setup-form]")));
        var form = provider.Find("form[data-testid=setup-form]");
        Assert.Equal("account/setup", form.GetAttribute("action"));
        Assert.Equal("one-time-code", form.QuerySelector("input[name=code]")?.GetAttribute("autocomplete"));
        Assert.NotNull(form.QuerySelector("input[name=newPassword]"));
        Assert.NotNull(form.QuerySelector("input[name=confirmPassword]"));
        Assert.Contains("at least 10 characters", provider.Find("[data-testid=setup-dialog]").TextContent);
        Assert.NotEmpty(provider.FindAll("[data-testid=unencrypted-http-warning]"));
    }
}
