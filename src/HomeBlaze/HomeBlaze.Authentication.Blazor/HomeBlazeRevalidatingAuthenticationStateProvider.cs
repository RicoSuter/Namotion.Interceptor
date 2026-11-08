using System.Security.Claims;
using HomeBlaze.Authentication.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// The authentication state of a circuit, which becomes anonymous once the session of the person ends: checked against
/// the <see cref="UserStore"/> in memory, see <see cref="UserStore.IsSessionAllowed"/>, when the state is first set and at
/// once when the store changes the person, and against the security stamp in the database every five minutes, which
/// catches a change made another way, such as by restoring the database.
/// </summary>
internal sealed partial class HomeBlazeRevalidatingAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<IdentityOptions> _identityOptions;
    private readonly UserStore _userStore;
    private readonly ILogger _logger;
    private volatile bool _isDisposed;
    private int _isStateSet;
    private volatile Task _lastRevalidation = Task.CompletedTask;

    public HomeBlazeRevalidatingAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<IdentityOptions> identityOptions,
        UserStore userStore)
        : base(loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(identityOptions);
        ArgumentNullException.ThrowIfNull(userStore);

        _scopeFactory = scopeFactory;
        _identityOptions = identityOptions;
        _userStore = userStore;
        _logger = loggerFactory.CreateLogger<HomeBlazeRevalidatingAuthenticationStateProvider>();
        _userStore.UserChanged += OnUserChanged;
        AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    /// <summary>The revalidation started by the latest change of a user, or by the first state set.</summary>
    internal Task LastRevalidation => _lastRevalidation;

    /// <inheritdoc />
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        ValidateAsync(authenticationState.User, cancellationToken);

    /// <summary>Whether <paramref name="principal"/> names a user in the database whose security stamp it holds.</summary>
    internal async Task<bool> ValidateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        cancellationToken.ThrowIfCancellationRequested();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return false;
        }

        if (!userManager.SupportsUserSecurityStamp)
        {
            return true;
        }

        var principalStamp = principal.FindFirstValue(_identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
        return principalStamp is not null && string.Equals(principalStamp, await userManager.GetSecurityStampAsync(user), StringComparison.Ordinal);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isDisposed = true;
            _userStore.UserChanged -= OnUserChanged;
            AuthenticationStateChanged -= OnAuthenticationStateChanged;
        }

        base.Dispose(disposing);
    }

    // Raised by the store outside its gate; the check runs in the background so that the store is not held up.
    private void OnUserChanged(string userId) => _lastRevalidation = RevalidateAsync(userId);

    // A change of the person between the request that carried the cookie and the start of the circuit raised no change
    // for this circuit, so the first state is checked as well.
    private void OnAuthenticationStateChanged(Task<AuthenticationState> state)
    {
        if (Interlocked.Exchange(ref _isStateSet, 1) == 0)
        {
            _lastRevalidation = RevalidateAsync(userId: null);
        }
    }

    // Checks the state of the circuit against the store when it names the user with userId, or any user when it is null.
    private async Task RevalidateAsync(string? userId)
    {
        Task<AuthenticationState> state;
        try
        {
            state = GetAuthenticationStateAsync();
        }
        catch (InvalidOperationException)
        {
            // No state is set yet, so nobody is signed in on this circuit.
            return;
        }

        bool isValid;
        try
        {
            var principal = (await state).User;
            var claimTypes = _identityOptions.Value.ClaimsIdentity;
            var principalUserId = principal.FindFirstValue(claimTypes.UserIdClaimType);
            if (principal.Identity?.IsAuthenticated != true ||
                (userId is not null && !string.Equals(principalUserId, userId, StringComparison.Ordinal)))
            {
                return;
            }

            isValid = principalUserId is not null &&
                _userStore.IsSessionAllowed(principalUserId, principal.FindFirstValue(claimTypes.SecurityStampClaimType));
        }
        catch (Exception exception)
        {
            LogRevalidationFailed(_logger, exception);
            isValid = false;
        }

        // A state set meanwhile, such as by another sign-in, is kept: only the state that was checked is replaced.
        if (!isValid && !_isDisposed && ReferenceEquals(state, GetAuthenticationStateAsync()))
        {
            SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The authentication state could not be revalidated; the circuit is signed out.")]
    private static partial void LogRevalidationFailed(ILogger logger, Exception exception);
}
