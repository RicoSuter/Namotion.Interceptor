using HomeBlaze.Authorization.Context;
using HomeBlaze.Authorization.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace HomeBlaze.Authorization.Blazor;

/// <summary>
/// Circuit handler that populates AuthorizationContext for Blazor Server circuits.
/// This ensures authorization context is available during Blazor component rendering
/// and event handling.
/// </summary>
internal class AuthorizationCircuitHandler : CircuitHandler
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly IRoleExpander _roleExpander;

    public AuthorizationCircuitHandler(
        AuthenticationStateProvider authenticationStateProvider,
        IRoleExpander roleExpander)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _roleExpander = roleExpander;
    }

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        await UpdateAuthorizationContextAsync();
        await base.OnCircuitOpenedAsync(circuit, cancellationToken);
    }

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        await UpdateAuthorizationContextAsync();
        await base.OnConnectionUpAsync(circuit, cancellationToken);
    }

    private async Task UpdateAuthorizationContextAsync()
    {
        var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        AuthorizationContext.PopulateFromUser(authState.User, _roleExpander.ExpandRoles);
    }
}
