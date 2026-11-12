using Bunit;
using HomeBlaze.Authentication.Blazor.Components;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public sealed class UserMenuTests : AccountComponentsTestContext
{
    [Fact]
    public async Task WhenAPersonIsSignedIn_ThenTheMenuShowsTheirNameTheAccountEntryAndASignOutFormWithAnAntiforgeryToken()
    {
        // Arrange
        await UseGraphAsync(roles: [BuiltInRoles.ReaderName]);
        var menu = RenderMenu();

        // Act
        await menu.Find("[data-testid=user-menu] button").ClickAsync(new());

        // Assert
        Assert.Equal(PermissionsTestGraph.TestUserName, menu.Find("[data-testid=user-menu-name]").TextContent.Trim());
        Assert.Empty(menu.FindAll("[data-testid=user-menu-user-name]"));
        Assert.Equal(PermissionsTestGraph.TestUserName, menu.Find("[data-testid=user-menu]").GetAttribute("title"));
        Assert.Null(menu.Find("[data-testid=user-menu-account]").GetAttribute("href"));
        var form = menu.Find("form[data-testid=user-menu-sign-out]");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("account/logout", form.GetAttribute("action"));
        var token = form.QuerySelector($"input[type=hidden][name={TokenFieldName}]");
        Assert.NotNull(token);
        Assert.Equal(TokenValue, token.GetAttribute("value"));
        Assert.NotNull(form.QuerySelector("button[type=submit]"));
        Assert.Empty(menu.FindAll("[data-testid=user-menu-sign-in]"));
    }

    [Fact]
    public async Task WhenThePersonHasADisplayName_ThenTheMenuShowsItWithTheUserNameBelowAndInTheTooltip()
    {
        // Arrange
        var graph = await UseGraphAsync(roles: [BuiltInRoles.ReaderName]);
        await SetDisplayNameAsync(graph, "Rico Müller");
        var menu = RenderMenu();

        // Act
        await menu.Find("[data-testid=user-menu] button").ClickAsync(new());

        // Assert
        Assert.Equal("Rico Müller", menu.Find("[data-testid=user-menu-name]").TextContent.Trim());
        Assert.Equal(PermissionsTestGraph.TestUserName, menu.Find("[data-testid=user-menu-user-name]").TextContent.Trim());
        Assert.Equal($"Rico Müller ({PermissionsTestGraph.TestUserName})", menu.Find("[data-testid=user-menu]").GetAttribute("title"));
    }

    [Fact]
    public async Task WhenTheDisplayNameChangesWhileTheMenuIsShown_ThenItShowsTheNewName()
    {
        // Arrange
        var graph = await UseGraphAsync(roles: [BuiltInRoles.ReaderName]);
        var menu = RenderMenu();
        Assert.Equal(PermissionsTestGraph.TestUserName, menu.Find("[data-testid=user-menu]").GetAttribute("title"));

        // Act
        await SetDisplayNameAsync(graph, "Rico");

        // Assert
        await menu.WaitForAssertionAsync(() =>
            Assert.Equal($"Rico ({PermissionsTestGraph.TestUserName})", menu.Find("[data-testid=user-menu]").GetAttribute("title")));
    }

    [Fact]
    public async Task WhenNobodyIsSignedIn_ThenTheMenuOffersOnlySigningInInADialog()
    {
        // Arrange
        await UseGraphAsync(roles: null);
        var menu = RenderMenu();
        Assert.Empty(menu.FindAll("[data-testid=user-menu]"));

        // Act
        await menu.Find("[data-testid=user-menu-sign-in]").ClickAsync(new());

        // Assert
        Assert.Null(menu.Find("[data-testid=user-menu-sign-in]").GetAttribute("href"));
        await menu.WaitForAssertionAsync(() => Assert.NotEmpty(menu.FindAll("[data-testid=sign-in-dialog]")));
        Assert.NotEmpty(menu.FindAll(".mud-dialog .mud-button-close"));
    }

    [Fact]
    public async Task WhenThePersonIsSignedOutOnTheCircuit_ThenTheMenuRendersAgainWithSigningIn()
    {
        // Arrange
        var graph = await UseGraphAsync(roles: [BuiltInRoles.ReaderName]);
        var menu = RenderMenu();
        Assert.NotEmpty(menu.FindAll("[data-testid=user-menu]"));

        // Act
        graph.SignOut();

        // Assert
        await menu.WaitForAssertionAsync(() =>
        {
            Assert.Empty(menu.FindAll("[data-testid=user-menu]"));
            Assert.NotEmpty(menu.FindAll("[data-testid=user-menu-sign-in]"));
        });
    }

    private static async Task SetDisplayNameAsync(PermissionsTestGraph graph, string displayName)
    {
        var result = await graph.UserStore.SetOwnDisplayNameAsync(graph.UserStore.GetUserId(PermissionsTestGraph.TestUserName)!, displayName, CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
    }

    // The menu within the dialog and popover providers, as the layout of the app renders it.
    private IRenderedComponent<CascadingAuthenticationState> RenderMenu() =>
        Render<CascadingAuthenticationState>(parameters => parameters.AddChildContent(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudBlazor.MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<UserMenu>(2);
            builder.CloseComponent();
        }));
}
