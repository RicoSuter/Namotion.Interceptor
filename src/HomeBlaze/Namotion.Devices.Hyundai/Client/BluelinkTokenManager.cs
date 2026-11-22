using System.Net;
using System.Runtime.ExceptionServices;

namespace Namotion.Devices.Hyundai.Client;

/// <summary>
/// Caches the CCS access token and refreshes the token set 6 minutes before the CCI or CCS token expires; the server
/// rejects tokens earlier than their stated lifetime. The rotated set is persisted before the new access token is
/// handed out. A rejected refresh (a 4xx answer other than 408 and 429) falls back to one sign in through the login
/// delegate; every other failure propagates unchanged for the caller to retry later. Not thread-safe: call it only
/// while the account lock is held.
/// </summary>
internal sealed class BluelinkTokenManager
{
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(1);

    private readonly BluelinkAuthClient _authClient;
    private readonly string _deviceId;
    private readonly TimeProvider _timeProvider;
    private readonly Func<BluelinkTokenSet, Task> _persistTokens;
    private readonly Func<CancellationToken, Task<BluelinkCciTokens>> _login;

    private BluelinkTokenSet _tokens;
    private bool _isPersisted = true;
    private string? _accessToken;
    private DateTimeOffset _refreshAt;
    private DateTimeOffset? _unpersistedAuthenticatedAt;

    /// <param name="authClient">The auth client.</param>
    /// <param name="tokens">The stored token set.</param>
    /// <param name="deviceId">The registered device ID.</param>
    /// <param name="timeProvider">The clock of the token lifetimes.</param>
    /// <param name="persistTokens">
    /// Stores a rotated token set. When it throws, the access token is not handed out, and the next call runs only this
    /// callback again, without a refresh.
    /// </param>
    /// <param name="login">
    /// Signs in again after a rejected refresh; throws <see cref="BluelinkReauthenticationRequiredException"/> when it
    /// cannot sign in, for example because no password is stored.
    /// </param>
    public BluelinkTokenManager(
        BluelinkAuthClient authClient,
        BluelinkTokenSet tokens,
        string deviceId,
        TimeProvider timeProvider,
        Func<BluelinkTokenSet, Task> persistTokens,
        Func<CancellationToken, Task<BluelinkCciTokens>> login)
    {
        _authClient = authClient;
        _tokens = tokens;
        _deviceId = deviceId;
        _timeProvider = timeProvider;
        _persistTokens = persistTokens;
        _login = login;
    }

    /// <summary>The device ID the manager signs with; vehicle API calls with its access tokens send it.</summary>
    public string DeviceId => _deviceId;

    /// <summary>The time of the last successful refresh or login, or null.</summary>
    public DateTimeOffset? AuthenticatedAt { get; private set; }

    /// <summary>Calls the vehicle API with an access token; after a token expired answer it refreshes once and calls again.</summary>
    /// <exception cref="BluelinkReauthenticationRequiredException">The refresh is rejected, and the sign in is rejected or not possible.</exception>
    /// <exception cref="BluelinkApiException">Any other failure, including a token expired answer after the refresh; the caller retries later.</exception>
    /// <exception cref="BluelinkCredentialsReplacedException">The login delegate refused to sign in, because the credentials were replaced.</exception>
    /// <exception cref="Exception">The exception of a failed persist callback; the next call retries only the persist.</exception>
    public async Task<T> CallAsync<T>(Func<string, Task<T>> call, CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        try
        {
            return await call(accessToken);
        }
        catch (BluelinkApiException exception) when (exception.IsTokenExpired)
        {
            _accessToken = null;
        }

        accessToken = await GetAccessTokenAsync(cancellationToken);
        return await call(accessToken);
    }

    /// <summary>Calls the vehicle API without a result.</summary>
    public Task CallAsync(Func<string, Task> call, CancellationToken cancellationToken) =>
        CallAsync<bool>(async accessToken =>
        {
            await call(accessToken);
            return true;
        }, cancellationToken);

    /// <summary>Makes sure a fresh access token is available, so that a refresh or sign in fails here and not inside a vehicle call.</summary>
    /// <exception cref="BluelinkReauthenticationRequiredException">The refresh is rejected, and the sign in is rejected or not possible.</exception>
    /// <exception cref="BluelinkApiException">The refresh or the token exchange failed otherwise; the caller retries later.</exception>
    /// <exception cref="BluelinkCredentialsReplacedException">The login delegate refused to sign in, because the credentials were replaced.</exception>
    /// <exception cref="Exception">The exception of a failed persist callback; the next call retries only the persist.</exception>
    public Task EnsureAccessTokenAsync(CancellationToken cancellationToken) => GetAccessTokenAsync(cancellationToken);

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!_isPersisted)
        {
            await PersistAsync();
        }

        if (_accessToken is not null && _timeProvider.GetUtcNow() < _refreshAt)
        {
            return _accessToken;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Not canceled from here on: a sent refresh has rotated the tokens, and the rotated set must be persisted.
        var cciTokens = await RefreshOrLoginAsync();
        _tokens = cciTokens.Tokens;
        _isPersisted = false;
        _accessToken = null;
        _unpersistedAuthenticatedAt = null;

        ExceptionDispatchInfo? exchangeFailure = null;
        try
        {
            var ccsToken = await _authClient.ExchangeCcsAsync(_tokens, _deviceId, CancellationToken.None);
            var now = _timeProvider.GetUtcNow();
            var refreshAt = Min(now + cciTokens.ExpiresIn, ccsToken.ExpiresAt) - RefreshAhead;
            _refreshAt = refreshAt > now + MinimumLifetime ? refreshAt : now + MinimumLifetime;
            _accessToken = ccsToken.AccessToken;
            _unpersistedAuthenticatedAt = now;
        }
        catch (Exception exception)
        {
            exchangeFailure = ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            await PersistAsync();
        }
        catch (Exception) when (exchangeFailure is not null)
        {
            // The exchange failure is the one reported; the next call persists again before it refreshes.
        }

        exchangeFailure?.Throw();
        return _accessToken!;
    }

    private async Task<BluelinkCciTokens> RefreshOrLoginAsync()
    {
        try
        {
            return await _authClient.RefreshAsync(_tokens, _deviceId, CancellationToken.None);
        }
        catch (BluelinkApiException exception) when (IsRefreshRejected(exception))
        {
            return await _login(CancellationToken.None);
        }
    }

    private static bool IsRefreshRejected(BluelinkApiException exception) =>
        !exception.IsFingerprintRejected &&
        exception.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError
            and not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests;

    private async Task PersistAsync()
    {
        await _persistTokens(_tokens);
        _isPersisted = true;
        if (_unpersistedAuthenticatedAt is { } authenticatedAt)
        {
            AuthenticatedAt = authenticatedAt;
            _unpersistedAuthenticatedAt = null;
        }
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;
}
