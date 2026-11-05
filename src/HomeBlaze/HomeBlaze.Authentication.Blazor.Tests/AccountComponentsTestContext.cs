using Bunit;
using HomeBlaze.Authentication.Blazor.Components;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>
/// A bUnit context for the account components over a <see cref="PermissionsTestGraph"/>: the services of a circuit of its
/// viewer, MudBlazor and an antiforgery token.
/// </summary>
public abstract class AccountComponentsTestContext : BunitContext, IAsyncLifetime
{
    public const string TokenFieldName = "__RequestVerificationToken";
    public const string TokenValue = "test-antiforgery-token";

    private PermissionsTestGraph? _graph;

    protected AccountComponentsTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<AntiforgeryStateProvider>(new TestAntiforgeryStateProvider());
        Services.AddScoped<RequestTransport>();
        Services.AddScoped(provider => new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance, provider));
        Services.AddScoped(provider => provider.GetRequiredService<ComponentStatePersistenceManager>().State);
    }

    internal PermissionsTestGraph Graph => _graph ?? throw new InvalidOperationException("Call UseGraphAsync first.");

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        if (_graph is not null)
        {
            await _graph.DisposeAsync();
        }
    }

    /// <summary>Creates the graph, see <see cref="PermissionsTestGraph.CreateAsync"/>, and registers its services of the viewer.</summary>
    internal async Task<PermissionsTestGraph> UseGraphAsync(IReadOnlyCollection<string>? roles, IReadOnlyCollection<string>? anonymousRoles = null)
    {
        _graph = await PermissionsTestGraph.CreateAsync(roles, anonymousRoles: anonymousRoles);
        Services.AddSingleton(_graph.CurrentUser);
        Services.AddSingleton<IUserPermissions>(_graph.Permissions);
        Services.AddSingleton(_graph.Services.GetRequiredService<IAuthorizationService>());
        Services.AddSingleton(_graph.Services.GetRequiredService<AccountPasswords>());
        Services.AddSingleton(_graph.AuthenticationStateProvider);
        return _graph;
    }

    /// <summary>Renders the dialog provider within the cascading authentication state, as the layout of the app does.</summary>
    internal IRenderedComponent<CascadingAuthenticationState> RenderDialogProvider() =>
        Render<CascadingAuthenticationState>(parameters => parameters.AddChildContent(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
        }));

    private sealed class TestAntiforgeryStateProvider : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken GetAntiforgeryToken() => new(TokenValue, TokenFieldName);
    }
}

/// <summary>A layout like the main layout, which marks whether it renders only its frame.</summary>
public sealed class TestShellLayout : LayoutComponentBase
{
    [CascadingParameter(Name = LayoutFrame.CascadingValueName)]
    private bool IsFrameOnly { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<MudDialogProvider>(1);
        builder.CloseComponent();
        builder.OpenElement(2, "div");
        builder.AddAttribute(3, "data-testid", "shell");
        builder.AddAttribute(4, "data-frame-only", IsFrameOnly ? "true" : "false");
        builder.AddContent(5, Body);
        builder.CloseElement();
    }
}
