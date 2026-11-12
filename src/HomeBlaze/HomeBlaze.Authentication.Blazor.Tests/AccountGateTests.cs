using Bunit;
using HomeBlaze.Authentication.Blazor.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public sealed class AccountGateTests : AccountComponentsTestContext
{
    private const string AppMarkup = "<p data-testid=\"app\">the app</p>";

    [Fact]
    public async Task WhenAVisitorWithoutRolesIsNotSignedIn_ThenOnlyTheFrameAndTheSignInDialogShow()
    {
        // Arrange
        await UseGraphAsync(roles: null);

        // Act
        var gate = RenderGate();

        // Assert
        await gate.WaitForAssertionAsync(() => Assert.NotEmpty(gate.FindAll("[data-testid=sign-in-dialog]")));
        Assert.Equal("true", gate.Find("[data-testid=shell]").GetAttribute("data-frame-only"));
        Assert.Empty(gate.FindAll("[data-testid=app]"));
        Assert.Empty(gate.FindAll(".mud-dialog .mud-button-close"));
        Assert.Equal("/", gate.Find("[data-testid=sign-in-form] input[name=returnUrl]").GetAttribute("value"));
    }

    [Fact]
    public async Task WhenAVisitorOpensASetupLink_ThenTheSetupDialogShowsWithTheLinkFilledIn()
    {
        // Arrange
        await UseGraphAsync(roles: null);
        Services.GetRequiredService<NavigationManager>().NavigateTo("account/setup?user=mom&code=ABCD1234");

        // Act
        var gate = RenderGate();

        // Assert
        await gate.WaitForAssertionAsync(() => Assert.NotEmpty(gate.FindAll("[data-testid=setup-dialog]")));
        Assert.Equal("mom", gate.Find("#setup-user-name").GetAttribute("value"));
        Assert.Equal("ABCD1234", gate.Find("#setup-code").GetAttribute("value"));
        Assert.Empty(gate.FindAll("[data-testid=sign-in-dialog]"));
        Assert.Empty(gate.FindAll("[data-testid=app]"));
    }

    [Fact]
    public async Task WhenAPersonMustChangeTheirPassword_ThenOnlyTheSetPasswordDialogShowsAndItCannotBeClosed()
    {
        // Arrange
        var graph = await UseGraphAsync(roles: [BuiltInRoles.EverythingName]);
        await graph.SignInTestUserAsync(mustChangePassword: true);

        // Act
        var gate = RenderGate();

        // Assert
        await gate.WaitForAssertionAsync(() => Assert.NotEmpty(gate.FindAll("[data-testid=set-password-form]")));
        Assert.Contains(PermissionsTestGraph.TestUserName, gate.Find("[data-testid=set-password-dialog]").TextContent);
        Assert.Empty(gate.FindAll(".mud-dialog .mud-button-close"));
        Assert.NotEmpty(gate.FindAll("form[data-testid=set-password-sign-out]"));
        Assert.Empty(gate.FindAll("[data-testid=app]"));
    }

    [Fact]
    public async Task WhenAPersonIsSignedIn_ThenTheAppShowsWithoutADialog()
    {
        // Arrange
        await UseGraphAsync(roles: [BuiltInRoles.ReaderName]);

        // Act
        var gate = RenderGate();

        // Assert
        await gate.WaitForAssertionAsync(() => Assert.NotEmpty(gate.FindAll("[data-testid=app]")));
        Assert.Empty(gate.FindAll("[data-testid=shell]"));
        Assert.Empty(gate.FindAll(".mud-dialog"));
    }

    [Fact]
    public async Task WhenAnonymousHoldsRoles_ThenTheAppShowsToAVisitorWhoIsNotSignedIn()
    {
        // Arrange
        await UseGraphAsync(roles: null, anonymousRoles: [BuiltInRoles.ReaderName]);

        // Act
        var gate = RenderGate();

        // Assert
        await gate.WaitForAssertionAsync(() => Assert.NotEmpty(gate.FindAll("[data-testid=app]")));
        Assert.Empty(gate.FindAll("[data-testid=shell]"));
    }

    [Fact]
    public async Task WhenTheAuthenticationStateIsNotKnownYet_ThenNothingRenders()
    {
        // Arrange
        await UseGraphAsync(roles: null, anonymousRoles: [BuiltInRoles.ReaderName]);
        var pending = new TaskCompletionSource<AuthenticationState>();

        // Act
        var gate = Render<CascadingValue<Task<AuthenticationState>>>(parameters => parameters
            .Add(value => value.Value, pending.Task)
            .AddChildContent<AccountGate>(gateParameters => gateParameters
                .Add(component => component.ShellLayout, typeof(TestShellLayout))
                .AddChildContent(AppMarkup)));

        // Assert
        Assert.Empty(gate.Markup.Trim());
    }

    [Fact]
    public async Task WhenAnonymousGetsAndLosesRolesWhileTheGateShows_ThenItFollowsLive()
    {
        // Arrange
        var graph = await UseGraphAsync(roles: null);
        var gate = RenderGate();
        await gate.WaitForAssertionAsync(() => Assert.NotEmpty(gate.FindAll("[data-testid=sign-in-dialog]")));
        var administration = new UserAdministration(graph.UserStore, () => graph.UserStore.GetUserId(AdminSeeding.AdminUserName));

        // Act
        var granted = await administration.SetRolesAsync(NameRules.AnonymousUserName, [BuiltInRoles.ReaderName], CancellationToken.None);

        // Assert
        Assert.True(granted.Succeeded, granted.Error);
        await gate.WaitForAssertionAsync(() =>
        {
            Assert.NotEmpty(gate.FindAll("[data-testid=app]"));
            Assert.Empty(gate.FindAll("[data-testid=shell]"));
        });

        // Act
        var removed = await administration.SetRolesAsync(NameRules.AnonymousUserName, [], CancellationToken.None);

        // Assert
        Assert.True(removed.Succeeded, removed.Error);
        await gate.WaitForAssertionAsync(() =>
        {
            Assert.Empty(gate.FindAll("[data-testid=app]"));
            Assert.NotEmpty(gate.FindAll("[data-testid=sign-in-dialog]"));
        });
    }

    [Fact]
    public async Task WhenTheRequiredPasswordChangeOfThePersonChanges_ThenTheGateFollowsLive()
    {
        // Arrange
        var graph = await UseGraphAsync(roles: [BuiltInRoles.ReaderName]);
        var gate = RenderGate();
        await gate.WaitForAssertionAsync(() => Assert.NotEmpty(gate.FindAll("[data-testid=app]")));

        // Act
        await graph.SignInTestUserAsync(mustChangePassword: true);

        // Assert
        await gate.WaitForAssertionAsync(() =>
        {
            Assert.Empty(gate.FindAll("[data-testid=app]"));
            Assert.NotEmpty(gate.FindAll("[data-testid=set-password-dialog]"));
        });

        // Act
        await graph.SignInTestUserAsync(mustChangePassword: false);

        // Assert
        await gate.WaitForAssertionAsync(() =>
        {
            Assert.NotEmpty(gate.FindAll("[data-testid=app]"));
            Assert.Empty(gate.FindAll(".mud-dialog"));
        });
    }

    [Fact]
    public async Task WhenTheGateAllowsAVisitorOnTheSetupPage_ThenTheResponseIsNeitherCachedNorSentOnAsAReferrer()
    {
        // Arrange
        await UseGraphAsync(roles: null, anonymousRoles: [BuiltInRoles.ReaderName]);
        Services.GetRequiredService<NavigationManager>().NavigateTo("account/setup?user=mom&code=ABCD1234");
        var context = new DefaultHttpContext();

        // Act
        var gate = RenderGate(context);

        // Assert
        await gate.WaitForAssertionAsync(() => Assert.NotEmpty(gate.FindAll("[data-testid=app]")));
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"]);
        Assert.Equal("no-cache, no-store", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
    }

    [Fact]
    public async Task WhenTheGateAllowsAVisitorElsewhere_ThenNoReferrerPolicyIsSet()
    {
        // Arrange
        await UseGraphAsync(roles: null, anonymousRoles: [BuiltInRoles.ReaderName]);
        var context = new DefaultHttpContext();

        // Act
        var gate = RenderGate(context);

        // Assert
        await gate.WaitForAssertionAsync(() => Assert.NotEmpty(gate.FindAll("[data-testid=app]")));
        Assert.False(context.Response.Headers.ContainsKey("Referrer-Policy"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenTheAuthorizationDecisionFails_ThenNothingRenders(bool completesAfterTheGateAllowed)
    {
        // Arrange
        var graph = await UseGraphAsync(roles: [BuiltInRoles.ReaderName]);
        var authorization = new SwitchableAuthorizationService(graph.Services.GetRequiredService<IAuthorizationService>());
        Services.AddSingleton<IAuthorizationService>(authorization);
        var gate = RenderGate();
        await gate.WaitForAssertionAsync(() => Assert.NotEmpty(gate.FindAll("[data-testid=app]")));

        // Act
        authorization.Failure = completesAfterTheGateAllowed ? new OperationCanceledException() : new InvalidOperationException("failed");
        await graph.SignInTestUserAsync(mustChangePassword: false);

        // Assert
        await gate.WaitForAssertionAsync(() => Assert.Empty(gate.FindAll("[data-testid=app]")));
        Assert.Empty(gate.Markup.Trim());
    }

    [Fact]
    public async Task WhenAStaticPageAllowsAnonymousItself_ThenItPassesTheGate()
    {
        // Arrange
        await UseGraphAsync(roles: null);
        var context = CreateContextOfStaticPage(typeof(AnonymousStaticPage));

        // Act
        var gate = RenderGate(context);

        // Assert
        await gate.WaitForAssertionAsync(() => Assert.NotEmpty(gate.FindAll("[data-testid=app]")));
        Assert.Empty(gate.FindAll("[data-testid=sign-in-dialog]"));
    }

    [Fact]
    public async Task WhenAStaticPageDoesNotAllowAnonymous_ThenTheGateStopsTheVisitor()
    {
        // Arrange
        await UseGraphAsync(roles: null);
        var context = CreateContextOfStaticPage(typeof(ProtectedStaticPage));

        // Act
        var gate = RenderGate(context);

        // Assert
        await gate.WaitForAssertionAsync(() => Assert.NotEmpty(gate.FindAll("[data-testid=sign-in-dialog]")));
        Assert.Empty(gate.FindAll("[data-testid=app]"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task WhenTheGateRendersARequest_ThenItRemembersWhetherTheServerSawHttps(bool isHttps, bool expected)
    {
        // Arrange
        await UseGraphAsync(roles: null, anonymousRoles: [BuiltInRoles.ReaderName]);
        var context = new DefaultHttpContext();
        context.Request.Scheme = isHttps ? "https" : "http";

        // Act
        var gate = RenderGate(context);

        // Assert
        await gate.WaitForAssertionAsync(() => Assert.NotEmpty(gate.FindAll("[data-testid=app]")));
        Assert.Equal(expected, Services.GetRequiredService<RequestTransport>().ServerSawHttps);
    }

    private static DefaultHttpContext CreateContextOfStaticPage(Type pageType)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(new ComponentTypeMetadata(pageType)),
            displayName: "page"));
        return context;
    }

    private IRenderedComponent<CascadingAuthenticationState> RenderGate(HttpContext? httpContext = null) =>
        Render<CascadingAuthenticationState>(parameters => parameters.AddChildContent<CascadingValue<HttpContext?>>(cascading => cascading
            .Add(value => value.Value, httpContext)
            .AddChildContent<AccountGate>(gateParameters => gateParameters
                .Add(component => component.ShellLayout, typeof(TestShellLayout))
                .AddChildContent(AppMarkup))));

    [AllowAnonymous]
    [ExcludeFromInteractiveRouting]
    private sealed class AnonymousStaticPage;

    [ExcludeFromInteractiveRouting]
    private sealed class ProtectedStaticPage;

    private sealed class SwitchableAuthorizationService(IAuthorizationService inner) : IAuthorizationService
    {
        public Exception? Failure { get; set; }

        public Task<Microsoft.AspNetCore.Authorization.AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Failure is { } failure ? Task.FromException<Microsoft.AspNetCore.Authorization.AuthorizationResult>(failure) : inner.AuthorizeAsync(user, resource, requirements);

        public Task<Microsoft.AspNetCore.Authorization.AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, string policyName) =>
            Failure is { } failure ? Task.FromException<Microsoft.AspNetCore.Authorization.AuthorizationResult>(failure) : inner.AuthorizeAsync(user, resource, policyName);
    }
}
