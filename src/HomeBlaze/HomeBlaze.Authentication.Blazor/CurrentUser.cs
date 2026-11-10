using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// The person of the current circuit, from its <see cref="AuthenticationStateProvider"/>, with the roles the
/// <see cref="UserStore"/> holds for them now. A visitor who is not signed in holds the roles of
/// <see cref="NameRules.AnonymousUserName"/>; a signed-in person the store no longer knows, and anybody while the
/// authentication state is not known, holds none. A person who must change their password holds their roles, but neither
/// <see cref="PolicyUserPermissions"/> nor the user administration of the circuit lets them act until they change it.
/// Scoped per circuit; thread-safe.
/// </summary>
internal sealed class CurrentUser : IDisposable
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly UserStore _userStore;
    private readonly ILogger<CurrentUser> _logger;
    private readonly string _userIdClaimType;
    private readonly Lock _lock = new();

    // Replaced as a whole under _lock, so that it is read without the lock.
    private volatile UserIdentity _identity = UserIdentity.Unknown;

    // The latest state of the provider; a state that completes after a later one was set is ignored. Guarded by _lock.
    private Task<AuthenticationState>? _latestState;

    public CurrentUser(
        AuthenticationStateProvider authenticationStateProvider,
        UserStore userStore,
        IOptions<IdentityOptions> identityOptions,
        ILogger<CurrentUser> logger)
    {
        ArgumentNullException.ThrowIfNull(authenticationStateProvider);
        ArgumentNullException.ThrowIfNull(userStore);
        ArgumentNullException.ThrowIfNull(identityOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _authenticationStateProvider = authenticationStateProvider;
        _userStore = userStore;
        _logger = logger;
        _userIdClaimType = identityOptions.Value.ClaimsIdentity.UserIdClaimType;

        _authenticationStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;
        _userStore.UserChanged += OnUserChanged;
        try
        {
            if (TryGetState(out var state))
            {
                Observe(state);
            }
        }
        catch
        {
            // Nobody disposes an instance whose construction failed, so the store and provider must not keep it.
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Raised when the person signs in or out, or when the store changes the person, or the visitor's built-in user. A
    /// handler that throws is logged and skips no other handler.
    /// </summary>
    public event Action? Changed;

    /// <summary>The id of the signed-in person; null when nobody is signed in or the state is not known yet.</summary>
    public string? UserId => _identity.UserId;

    /// <summary>The display name of the signed-in person as the store holds it now, or null.</summary>
    public string? DisplayName => _identity.UserId is { } userId ? _userStore.GetDisplayName(userId) : null;

    /// <summary>The role names held now, see <see cref="CurrentUser"/>.</summary>
    public IReadOnlyCollection<string> Roles => _identity.GetRoles(_userStore);

    /// <summary><see cref="PrincipalKind.Person"/> when signed in, else <see cref="PrincipalKind.Anonymous"/>.</summary>
    public PrincipalKind Kind => _identity.Kind;

    public void Dispose()
    {
        _authenticationStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        _userStore.UserChanged -= OnUserChanged;
    }

    /// <summary>
    /// Gets the roles and the kind of one identity, and whether that person must change their password, so that they never
    /// belong to different people.
    /// </summary>
    internal (IReadOnlyCollection<string> Roles, PrincipalKind Kind, bool MustChangePassword) GetPolicyInputs() =>
        _identity.GetPolicyInputs(_userStore);

    private bool TryGetState([NotNullWhen(true)] out Task<AuthenticationState>? state)
    {
        try
        {
            state = _authenticationStateProvider.GetAuthenticationStateAsync();
            return true;
        }
        catch (InvalidOperationException)
        {
            // The server provider throws until the circuit or the request sets the state, which raises the change event.
            state = null;
            return false;
        }
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> state) => Observe(state);

    private void Observe(Task<AuthenticationState> state)
    {
        lock (_lock)
        {
            _latestState = state;
        }

        if (state.IsCompleted)
        {
            Apply(state);
        }
        else
        {
            state.ContinueWith(
                static (completed, owner) => ((CurrentUser)owner!).Apply(completed),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void Apply(Task<AuthenticationState> state)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(state, _latestState))
            {
                return;
            }

            // A failed state names nobody and holds no roles.
            _identity = state.IsCompletedSuccessfully
                ? UserIdentity.From(state.Result.User, _userIdClaimType)
                : UserIdentity.Unknown;
        }

        ChangedHandlers.Raise(Changed, _logger, nameof(CurrentUser));
    }

    private void OnUserChanged(string userId)
    {
        var identity = _identity;
        var isAffected = identity.IsSignedIn
            ? string.Equals(identity.UserId, userId, StringComparison.Ordinal)
            : string.Equals(_userStore.GetUserId(NameRules.AnonymousUserName), userId, StringComparison.Ordinal);
        if (isAffected)
        {
            ChangedHandlers.Raise(Changed, _logger, nameof(CurrentUser));
        }
    }
}
