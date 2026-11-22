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

    /// <summary>The email claim of the last ID token, or null.</summary>
    public string? AuthenticatedUser => _state.AuthenticatedUser;

    /// <summary>The time of the last successful refresh, or null.</summary>
    public DateTimeOffset? AuthenticatedAt => _state.AuthenticatedAt;

    /// <summary>The token refresh in progress, or a completed task. It faults when the refresh fails.</summary>
    public Task PendingRefresh
    {
        get
        {
            lock (_lock)
            {
                return _refresh ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Calls the API with an access token. After a 401 it refreshes the token once and calls again; a second 401 is
    /// thrown as it is, because Tesla accepted the refresh token.
    /// </summary>
    /// <exception cref="TeslaReauthenticationRequiredException">Tesla rejected the refresh token.</exception>
    /// <exception cref="TeslaApiException">The call failed, also with a 401 after the refresh.</exception>
    public Task<T> CallAsync<T>(Func<string, Task<T>> call, CancellationToken cancellationToken) =>
        CallAsync(call, refreshOnUnauthorized: true, cancellationToken);

    /// <summary>
    /// Calls the API with an access token. With <paramref name="refreshOnUnauthorized"/>, a 401 refreshes the token once
    /// and calls again; without, a 401 is thrown as it is, for a call whose access token another call just accepted.
    /// </summary>
    /// <exception cref="TeslaReauthenticationRequiredException">Tesla rejected the refresh token.</exception>
    /// <exception cref="TeslaApiException">The call failed.</exception>
    public async Task<T> CallAsync<T>(Func<string, Task<T>> call, bool refreshOnUnauthorized, CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessTokenAsync(null, cancellationToken);
        if (!refreshOnUnauthorized)
        {
            return await call(accessToken);
        }

        try
        {
            return await call(accessToken);
        }
        catch (TeslaApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Passing the rejected token makes concurrent callers with the same rejected token share one refresh.
            accessToken = await GetAccessTokenAsync(accessToken, cancellationToken);
        }

        return await call(accessToken);
    }

    private async Task<string> GetAccessTokenAsync(string? rejectedAccessToken, CancellationToken cancellationToken)
    {
        var state = _state;
        if (IsUsable(state, rejectedAccessToken))
        {
            return state.AccessToken!;
        }

        // A canceled caller must not start a refresh, which could outlive a disposal that already checked for one.
        cancellationToken.ThrowIfCancellationRequested();

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
