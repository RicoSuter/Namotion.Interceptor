using System.Net;

namespace Namotion.Devices.Tesla.Client;

/// <summary>
/// Caches the access token and refreshes it when less than an hour or half its lifetime is left. Each refresh rotates
/// the refresh token: the rotation callback completes before the new access token is handed out, so that a crash never
/// loses the only valid refresh token. Thread-safe: concurrent callers share one refresh.
/// </summary>
internal sealed class TeslaTokenManager
{
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromHours(1);

    private readonly TeslaAuthClient _authClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, Task> _onRefreshTokenRotated;
    private readonly Lock _lock = new();

    // Replaced as a whole so that readers outside the lock never see a torn state; written only while _refresh runs.
    private volatile TokenState _state;
    private Task<TokenState>? _refresh;

    /// <param name="authClient">The client of the authorization server.</param>
    /// <param name="refreshToken">The current refresh token.</param>
    /// <param name="timeProvider">The clock for the expiry of the access token.</param>
    /// <param name="onRefreshTokenRotated">
    /// Persists the rotated refresh token. When it throws, the access token is not handed out and the next call runs
    /// only the callback again, without a token request, while the access token is still fresh.
    /// </param>
    public TeslaTokenManager(TeslaAuthClient authClient, string refreshToken, TimeProvider timeProvider, Func<string, Task> onRefreshTokenRotated)
    {
        _authClient = authClient;
        _timeProvider = timeProvider;
        _onRefreshTokenRotated = onRefreshTokenRotated;
        _state = new TokenState(refreshToken, null, default, true, null, null);
    }

    /// <summary>The current refresh token; it changes with every refresh.</summary>
    public string RefreshToken => _state.RefreshToken;

    /// <summary>The email claim of the last ID token, or null.</summary>
    public string? AuthenticatedUser => _state.AuthenticatedUser;

    /// <summary>The time of the last successful refresh, or null.</summary>
    public DateTimeOffset? AuthenticatedAt => _state.AuthenticatedAt;

    /// <summary>Returns a valid access token and refreshes it first when needed.</summary>
    /// <exception cref="TeslaReauthenticationRequiredException">Tesla rejected the refresh token.</exception>
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
        GetAccessTokenAsync(null, cancellationToken);

    /// <summary>
    /// Calls the API with an access token. After a 401 it refreshes the token once and calls again; a second 401
    /// requires re-authentication.
    /// </summary>
    /// <exception cref="TeslaReauthenticationRequiredException">Tesla rejected the refresh token or the refreshed access token.</exception>
    public async Task<T> CallAsync<T>(Func<string, Task<T>> call, CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessTokenAsync(null, cancellationToken);
        try
        {
            return await call(accessToken);
        }
        catch (TeslaApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Passing the rejected token makes concurrent callers with the same rejected token share one refresh.
            accessToken = await GetAccessTokenAsync(accessToken, cancellationToken);
        }

        try
        {
            return await call(accessToken);
        }
        catch (TeslaApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new TeslaReauthenticationRequiredException(exception.Error);
        }
    }

    private async Task<string> GetAccessTokenAsync(string? rejectedAccessToken, CancellationToken cancellationToken)
    {
        var state = _state;
        if (IsUsable(state, rejectedAccessToken))
        {
            return state.AccessToken!;
        }

        TaskCompletionSource<TokenState>? completion = null;
        Task<TokenState> refresh;
        lock (_lock)
        {
            state = _state;
            if (IsUsable(state, rejectedAccessToken))
            {
                return state.AccessToken!;
            }

            if (_refresh is null)
            {
                completion = new TaskCompletionSource<TokenState>(TaskCreationOptions.RunContinuationsAsynchronously);
                _refresh = completion.Task;
            }

            refresh = _refresh;
        }

        if (completion is not null)
        {
            // Started outside the lock and not awaited here, so that the callback never runs under the lock and a
            // canceled caller does not cancel the refresh. A fresh but unpersisted token only needs the callback.
            _ = CompleteAsync(state, requestTokens: !IsFresh(state, rejectedAccessToken), completion);
        }

        var refreshed = await refresh.WaitAsync(cancellationToken);
        return refreshed.AccessToken!;
    }

    /// <summary>
    /// Requests tokens when needed and persists the refresh token. Runs without cancellation: once the request is sent,
    /// Tesla may already have rotated the refresh token, and the rotated token must reach the callback. The HTTP client
    /// timeout bounds it.
    /// </summary>
    private async Task CompleteAsync(TokenState state, bool requestTokens, TaskCompletionSource<TokenState> completion)
    {
        try
        {
            if (requestTokens)
            {
                state = await RequestTokensAsync(state);
                _state = state;
            }

            await _onRefreshTokenRotated(state.RefreshToken);

            state = state with { IsPersisted = true };
            lock (_lock)
            {
                _state = state;
                _refresh = null;
            }

            completion.SetResult(state);
        }
        catch (Exception exception)
        {
            lock (_lock)
            {
                _refresh = null;
            }

            completion.SetException(exception);
        }
    }

    private async Task<TokenState> RequestTokensAsync(TokenState previous)
    {
        var tokens = await _authClient.RefreshAsync(previous.RefreshToken, _timeProvider, CancellationToken.None);
        var now = _timeProvider.GetUtcNow();
        var halfLifetime = (tokens.ExpiresAt - now) / 2;
        var refreshAt = tokens.ExpiresAt - (halfLifetime < RefreshAhead ? halfLifetime : RefreshAhead);
        return new TokenState(tokens.RefreshToken, tokens.AccessToken, refreshAt, false, tokens.Email ?? previous.AuthenticatedUser, now);
    }

    private bool IsUsable(TokenState state, string? rejectedAccessToken) =>
        state.IsPersisted && IsFresh(state, rejectedAccessToken);

    private bool IsFresh(TokenState state, string? rejectedAccessToken) =>
        state.AccessToken is not null &&
        state.AccessToken != rejectedAccessToken &&
        _timeProvider.GetUtcNow() < state.RefreshAt;

    private sealed record TokenState(
        string RefreshToken,
        string? AccessToken,
        DateTimeOffset RefreshAt,
        bool IsPersisted,
        string? AuthenticatedUser,
        DateTimeOffset? AuthenticatedAt);
}
