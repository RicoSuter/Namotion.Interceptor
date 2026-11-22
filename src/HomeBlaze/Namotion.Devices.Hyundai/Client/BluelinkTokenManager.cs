namespace Namotion.Devices.Hyundai.Client;

/// <summary>
/// Caches the CCS access token and refreshes the token set 6 minutes before the CCI or CCS token expires; the server
/// rejects tokens earlier than their stated lifetime. The rotated set is persisted before the new access token is
/// handed out. Not thread-safe: call it only while the account lock is held.
/// </summary>
internal sealed class BluelinkTokenManager
{
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(1);

    private readonly BluelinkAuthClient _authClient;
    private readonly string _deviceId;
    private readonly TimeProvider _timeProvider;
    private readonly Func<BluelinkTokenSet, Task> _persistTokens;
    private readonly Func<CancellationToken, Task<BluelinkCciTokens>>? _login;

    private BluelinkTokenSet _tokens;
    private bool _isPersisted = true;
    private string? _accessToken;
    private DateTimeOffset _refreshAt;

    /// <param name="authClient">The auth client.</param>
    /// <param name="tokens">The stored token set.</param>
    /// <param name="deviceId">The registered device ID.</param>
    /// <param name="timeProvider">The clock of the token lifetimes.</param>
    /// <param name="persistTokens">
    /// Stores a rotated token set. When it throws, the access token is not handed out, and the next call runs only this
    /// callback again, without a refresh.
    /// </param>
    /// <param name="login">Signs in with the stored password, or null when no password is stored.</param>
    public BluelinkTokenManager(
        BluelinkAuthClient authClient,
        BluelinkTokenSet tokens,
        string deviceId,
        TimeProvider timeProvider,
        Func<BluelinkTokenSet, Task> persistTokens,
        Func<CancellationToken, Task<BluelinkCciTokens>>? login)
    {
        _authClient = authClient;
        _tokens = tokens;
        _deviceId = deviceId;
        _timeProvider = timeProvider;
        _persistTokens = persistTokens;
        _login = login;
    }

    /// <summary>The time of the last successful refresh or login, or null.</summary>
    public DateTimeOffset? AuthenticatedAt { get; private set; }

    /// <summary>Calls the vehicle API with an access token; after a token expired answer it refreshes once and calls again.</summary>
    /// <exception cref="BluelinkReauthenticationRequiredException">The tokens and the stored password are rejected.</exception>
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
        try
        {
            return await call(accessToken);
        }
        catch (BluelinkApiException exception) when (exception.IsTokenExpired)
        {
            throw new BluelinkReauthenticationRequiredException("Hyundai rejected the refreshed access token.");
        }
    }

    /// <summary>Calls the vehicle API without a result.</summary>
    public Task CallAsync(Func<string, Task> call, CancellationToken cancellationToken) =>
        CallAsync<bool>(async accessToken =>
        {
            await call(accessToken);
            return true;
        }, cancellationToken);

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
        try
        {
            var ccsToken = await _authClient.ExchangeCcsAsync(_tokens, _deviceId, CancellationToken.None);
            var now = _timeProvider.GetUtcNow();
            var refreshAt = Min(now + cciTokens.ExpiresIn, ccsToken.ExpiresAt) - RefreshAhead;
            _refreshAt = refreshAt > now + MinimumLifetime ? refreshAt : now + MinimumLifetime;
            _accessToken = ccsToken.AccessToken;
            AuthenticatedAt = now;
        }
        finally
        {
            await PersistAsync();
        }

        return _accessToken;
    }

    private async Task<BluelinkCciTokens> RefreshOrLoginAsync()
    {
        try
        {
            return await _authClient.RefreshAsync(_tokens, _deviceId, CancellationToken.None);
        }
        catch (BluelinkApiException exception) when (!exception.IsFingerprintRejected)
        {
            if (_login is null)
            {
                throw new BluelinkReauthenticationRequiredException("Hyundai rejected the refresh token, and no password is stored.");
            }

            return await _login(CancellationToken.None);
        }
    }

    private async Task PersistAsync()
    {
        await _persistTokens(_tokens);
        _isPersisted = true;
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;
}
