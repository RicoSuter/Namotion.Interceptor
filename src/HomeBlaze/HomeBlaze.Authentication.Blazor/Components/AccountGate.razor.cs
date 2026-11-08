using System.Security.Claims;
using HomeBlaze.Authorization.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace HomeBlaze.Authentication.Blazor.Components;

/// <summary>
/// The one gate of the app: renders <see cref="ChildContent"/>, the router of the app, only to a person who passes
/// <see cref="HomeBlazeAccessRequirement"/> and need not change their password, as the <see cref="UserStore"/> holds now.
/// Anybody else gets only the frame of
/// <see cref="ShellLayout"/> with the dialog that signs them in, redeems their setup code on
/// <see cref="AccountEndpoints.SetupPath"/>, or sets their password; nothing while the authentication state is not known.
/// It decides again when the authentication state, the person or <c>anonymous</c> in the store, or the policies change.
/// A static page that allows anonymous access itself, such as the error page, passes, as no circuit opens for it.
/// </summary>
public sealed partial class AccountGate : ComponentBase, IDisposable
{
    private const string TransportStateKey = "HomeBlaze.AccountGate.ServerSawHttps";

    private AccountGateOutcome _outcome = AccountGateOutcome.Unknown;
    private string? _returnUrl;
    private string? _setupUserName;
    private string? _setupCode;
    private bool _isDisposed;
    private PersistingComponentStateSubscription _transportSubscription;

    // Counts the decisions, so that one that completes after a later one started is dropped.
    private int _decision;

    [Inject]
    private IAuthorizationService AuthorizationService { get; set; } = default!;

    [Inject]
    private IUserPermissions Permissions { get; set; } = default!;

    [Inject]
    private UserStore UserStore { get; set; } = default!;

    [Inject]
    private IOptions<IdentityOptions> IdentityOptions { get; set; } = default!;

    [Inject]
    private RequestTransport Transport { get; set; } = default!;

    [Inject]
    private PersistentComponentState PersistentState { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    // Resolved while prerendering, so that the antiforgery token of the forms of the dialogs reaches the circuit.
    [Inject]
    private AntiforgeryStateProvider AntiforgeryState { get; set; } = default!;

    [CascadingParameter]
    private Task<AuthenticationState>? AuthenticationState { get; set; }

    [CascadingParameter]
    private HttpContext? HttpContext { get; set; }

    /// <summary>The app, rendered only when the gate allows it.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// The layout that renders only its frame around the dialog when it gets the cascading value of
    /// <see cref="HomeBlaze.Components.Abstractions.Pages.LayoutFrame"/>.
    /// </summary>
    [Parameter, EditorRequired]
    public Type ShellLayout { get; set; } = default!;

    protected override void OnInitialized()
    {
        _ = AntiforgeryState.GetAntiforgeryToken();
        Permissions.Changed += OnPermissionsChanged;
        CarryTransportIntoCircuit();
    }

    // Only the request that renders the page knows whether it was HTTPS, so it is persisted into the circuit.
    private void CarryTransportIntoCircuit()
    {
        if (HttpContext is { } context)
        {
            Transport.ServerSawHttps = context.Request.IsHttps;
            _transportSubscription = PersistentState.RegisterOnPersisting(() =>
            {
                PersistentState.PersistAsJson(TransportStateKey, Transport.ServerSawHttps);
                return Task.CompletedTask;
            });
        }
        else if (PersistentState.TryTakeFromJson<bool>(TransportStateKey, out var serverSawHttps))
        {
            Transport.ServerSawHttps = serverSawHttps;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        await DecideAsync();
        if ((_outcome is not AccountGateOutcome.Allowed and not AccountGateOutcome.Unknown || IsSetupPath(new Uri(Navigation.Uri))) &&
            HttpContext is { Response.HasStarted: false } context)
        {
            // The dialogs take passwords and the setup page has its code in the address, so the page must neither be
            // cached nor sent on as a referrer, also where the gate lets a visitor through to it.
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        Permissions.Changed -= OnPermissionsChanged;
        _transportSubscription.Dispose();
    }

    // Raised for the person or Anonymous, and for the policies, from any thread. Renders only when the outcome changed:
    // rendering the router again would set the parameters of the shown page again.
    private void OnPermissionsChanged() => _ = InvokeAsync(async () =>
    {
        if (await DecideAsync() && !_isDisposed)
        {
            StateHasChanged();
        }
    });

    // Returns whether the outcome changed.
    private async Task<bool> DecideAsync()
    {
        var decision = ++_decision;
        AccountGateOutcome outcome;
        try
        {
            outcome = await GetOutcomeAsync();
        }
        catch (Exception)
        {
            // Fails closed: a decision that failed or was canceled shows nothing, so that one canceled after the gate
            // allowed the app does not leave it shown.
            outcome = AccountGateOutcome.Unknown;
        }

        if (_isDisposed || decision != _decision || outcome == _outcome)
        {
            return false;
        }

        _outcome = outcome;
        return true;
    }

    private async Task<AccountGateOutcome> GetOutcomeAsync()
    {
        if (IsStaticPageAllowingAnonymous())
        {
            return AccountGateOutcome.Allowed;
        }

        if (AuthenticationState is null)
        {
            return AccountGateOutcome.Unknown;
        }

        var user = (await AuthenticationState).User;
        if (UserIdentity.From(user, IdentityOptions.Value.ClaimsIdentity.UserIdClaimType).RequiresPasswordChange(UserStore))
        {
            _returnUrl = Navigation.GetPathAndQuery();
            return AccountGateOutcome.SetPassword;
        }

        if ((await AuthorizationService.AuthorizeAsync(user, resource: null, HomeBlazeAccessRequirement.PolicyName)).Succeeded)
        {
            return AccountGateOutcome.Allowed;
        }

        return GetSignedOutOutcome(user);
    }

    // A signed-in person always passes the policy, so this is a visitor who is not signed in.
    private AccountGateOutcome GetSignedOutOutcome(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated == true)
        {
            return AccountGateOutcome.Unknown;
        }

        var uri = new Uri(Navigation.Uri);
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (IsSetupPath(uri))
        {
            _setupUserName = query.TryGetValue("user", out var userName) ? userName.ToString() : null;
            _setupCode = query.TryGetValue("code", out var code) ? code.ToString() : null;
            return AccountGateOutcome.Setup;
        }

        // A challenge of another endpoint leads to the root with the address it came from.
        _returnUrl = query.TryGetValue("ReturnUrl", out var returnUrl) ? returnUrl.ToString() : uri.PathAndQuery;
        return AccountGateOutcome.SignIn;
    }

    private bool IsSetupPath(Uri uri) =>
        string.Equals(Navigation.ToBaseRelativePath(uri.GetLeftPart(UriPartial.Path)), AccountEndpoints.SetupPath.TrimStart('/'), StringComparison.OrdinalIgnoreCase);

    // Only while rendering a request: a page excluded from interactive routing never opens a circuit, so the gate decides
    // once, by the attribute of the page itself, as the endpoints of the app all allow anonymous requests.
    private bool IsStaticPageAllowingAnonymous() =>
        HttpContext is { } context &&
        !context.AcceptsInteractiveRouting() &&
        context.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>()?.Type is { } pageType &&
        Attribute.IsDefined(pageType, typeof(AllowAnonymousAttribute));
}
