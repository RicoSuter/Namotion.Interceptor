using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Tesla.Client;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Devices.Tesla;

public partial class TeslaAccount
{
    private const string ReauthenticationRequiredMessage = "Re-authentication required";

    // Guards RefreshToken together with _tokenManager and _tokenManagerRefreshToken. Without it, a rotation that
    // completes after its caller was canceled could make GetTokenManager see the rotated token in the manager but not
    // yet in RefreshToken, and replace the manager with one that holds the token Tesla just invalidated.
    private readonly Lock _refreshTokenLock = new();

    private TeslaTokenManager? _tokenManager;
    private string? _tokenManagerRefreshToken;

    /// <summary>
    /// Replaces the refresh token with one the user entered. A rotation of the previous token that completes later does
    /// not overwrite it. Apply the configuration afterwards so that polling uses the new token.
    /// </summary>
    public void ReplaceRefreshToken(string refreshToken)
    {
        lock (_refreshTokenLock)
        {
            RefreshToken = refreshToken;
        }
    }

    /// <summary>
    /// Returns the token manager. It is kept across polls and configuration changes, so that a restart of the loop does
    /// not refresh again, and replaced only when the configured refresh token was replaced.
    /// </summary>
    /// <remarks>Call it only while the poll lock is held.</remarks>
    private TeslaTokenManager GetTokenManager()
    {
        TeslaTokenManager? tokenManager = null;
        lock (_refreshTokenLock)
        {
            var refreshToken = RefreshToken;
            if (_tokenManager is not null && refreshToken == _tokenManagerRefreshToken)
            {
                return _tokenManager;
            }

            tokenManager = new TeslaTokenManager(_authClient, refreshToken, _timeProvider,
                rotatedRefreshToken => OnRefreshTokenRotatedAsync(tokenManager!, rotatedRefreshToken));

            _tokenManager = tokenManager;
            _tokenManagerRefreshToken = refreshToken;
        }

        // A replaced refresh token has not been rejected yet.
        IsReauthenticationRequired = false;
        return tokenManager;
    }

    /// <summary>
    /// Stores the rotated refresh token in the configuration before the token manager hands out the new access token.
    /// A failed write throws, so that the token manager keeps the token pending and retries only the write with the next
    /// call. Without a configuration writer that stores the account, the token is kept in memory only.
    /// </summary>
    private async Task OnRefreshTokenRotatedAsync(TeslaTokenManager tokenManager, string refreshToken)
    {
        lock (_refreshTokenLock)
        {
            // The configured refresh token was replaced while this rotation ran, also before the next poll replaces the
            // manager; the replacement wins. A retried write passes, because both then hold the rotated token.
            if (!ReferenceEquals(tokenManager, _tokenManager) || RefreshToken != _tokenManagerRefreshToken)
            {
                return;
            }

            RefreshToken = refreshToken;
            _tokenManagerRefreshToken = refreshToken;
        }

        var writer = this.TryGetFirstParent<IConfigurationWriter>();
        bool isWritten;
        try
        {
            // Not canceled: Tesla has already invalidated the previous refresh token.
            isWritten = writer is not null && await writer.WriteConfigurationAsync(this, CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("The rotated refresh token could not be saved; saving is retried with the next poll.", exception);
        }

        if (!isWritten)
        {
            _logger.LogWarning("The rotated Tesla refresh token is kept in memory only, because no configuration writer stores the account.");
        }
    }

    private void UpdateAuthenticationState(TeslaTokenManager tokenManager)
    {
        IsReauthenticationRequired = false;
        IsAuthenticated = true;
        AuthenticatedUser = tokenManager.AuthenticatedUser ?? AuthenticatedUser;
        AuthenticatedAt = tokenManager.AuthenticatedAt ?? AuthenticatedAt;
    }

    private void SetReauthenticationRequired(TeslaReauthenticationRequiredException exception)
    {
        _logger.LogWarning("Tesla rejected the refresh token ({Error}); polling waits for a new refresh token.", exception.Error);
        IsReauthenticationRequired = true;
        IsAuthenticated = false;
        Status = ServiceStatus.Error;
        StatusMessage = ReauthenticationRequiredMessage;
    }
}
