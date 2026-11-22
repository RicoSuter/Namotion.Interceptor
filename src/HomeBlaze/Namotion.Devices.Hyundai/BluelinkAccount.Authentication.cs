using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Hyundai.Client;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Devices.Hyundai;

public partial class BluelinkAccount
{
    private const string ReauthenticationRequiredMessage = "Re-authentication required";

    // Guards Email, Password, Pin, Tokens and DeviceId together with _tokenManager, _tokenManagerTokens and
    // _rejectedCredentials, so that a rotation that completes after the credentials were replaced never overwrites the
    // replacement.
    private readonly Lock _credentialsLock = new();

    private BluelinkTokenManager? _tokenManager;
    private string? _tokenManagerTokens;

    // The tokens and password that Hyundai rejected; polling waits until one of them differs.
    private (string Tokens, string Password)? _rejectedCredentials;

    // Set when a write of rotated tokens or a registered device ID was not stored; the next poll retries it.
    private volatile bool _isConfigurationUnsaved;

    /// <summary>
    /// Replaces the credentials with those of a connection from the setup or edit dialog. A rotation of the previous
    /// tokens that completes later does not overwrite them. Apply the configuration afterwards.
    /// </summary>
    public void ReplaceCredentials(BluelinkConnection connection)
    {
        lock (_credentialsLock)
        {
            Email = connection.Email;
            Password = connection.Password;
            Pin = connection.Pin;
            Tokens = connection.Tokens;
            DeviceId = connection.DeviceId;
        }
    }

    /// <summary>
    /// Returns the token manager, kept across polls and configuration changes and replaced only when the stored tokens
    /// were replaced.
    /// </summary>
    /// <remarks>Call it only while <see cref="_lock"/> is held.</remarks>
    /// <exception cref="BluelinkReauthenticationRequiredException">The stored tokens are not valid.</exception>
    private BluelinkTokenManager GetTokenManager()
    {
        BluelinkTokenManager? tokenManager = null;
        lock (_credentialsLock)
        {
            if (_tokenManager is not null && Tokens == _tokenManagerTokens)
            {
                return _tokenManager;
            }

            BluelinkTokenSet tokens;
            try
            {
                tokens = BluelinkTokenSet.Parse(Tokens);
            }
            catch (FormatException)
            {
                throw new BluelinkReauthenticationRequiredException("The stored tokens are not valid. Connect the account again.");
            }

            var deviceId = DeviceId;
            // The login reads the password when it runs, so that a changed password applies without new tokens.
            tokenManager = new BluelinkTokenManager(_authClient, tokens, deviceId, _timeProvider,
                rotatedTokens => PersistTokensAsync(tokenManager!, rotatedTokens),
                cancellationToken => LoginAsync(deviceId, cancellationToken));

            _tokenManager = tokenManager;
            _tokenManagerTokens = Tokens;
        }

        _controlToken = null;
        return tokenManager;
    }

    /// <summary>Signs in with the stored email and password after Hyundai rejected the refresh token.</summary>
    /// <exception cref="BluelinkReauthenticationRequiredException">No password is stored, or Hyundai rejected the sign in.</exception>
    private Task<BluelinkCciTokens> LoginAsync(string deviceId, CancellationToken cancellationToken)
    {
        string email;
        string password;
        lock (_credentialsLock)
        {
            email = Email;
            password = Password;
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new BluelinkReauthenticationRequiredException("Hyundai rejected the refresh token, and no password is stored.");
        }

        _logger.LogInformation("Hyundai rejected the Bluelink refresh token; signing in again with the stored password.");
        return _authClient.LoginAsync(email, password, deviceId, cancellationToken);
    }

    /// <summary>
    /// Stores rotated tokens in the configuration before the token manager hands out the new access token. A failed
    /// write throws, so that the manager retries only the write with the next call.
    /// </summary>
    private async Task PersistTokensAsync(BluelinkTokenManager tokenManager, BluelinkTokenSet tokens)
    {
        var serialized = tokens.Serialize();
        lock (_credentialsLock)
        {
            // The credentials were replaced while this rotation ran; the replacement wins. A retried write passes,
            // because both then hold the rotated tokens.
            if (!ReferenceEquals(tokenManager, _tokenManager) || Tokens != _tokenManagerTokens)
            {
                return;
            }

            Tokens = serialized;
            _tokenManagerTokens = serialized;
        }

        await WriteConfigurationAsync(throwOnFailure: true, CancellationToken.None);
    }

    /// <summary>Writes the configuration through the storage file that holds the account.</summary>
    /// <param name="throwOnFailure">Whether a failing writer throws; otherwise the failure is logged and retried with the next poll.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task WriteConfigurationAsync(bool throwOnFailure, CancellationToken cancellationToken)
    {
        var writer = this.TryGetFirstParent<IConfigurationWriter>();
        if (writer is null)
        {
            _isConfigurationUnsaved = false;
            _logger.LogWarning("The Bluelink tokens are kept in memory only, because no configuration writer stores the account.");
            return;
        }

        bool isWritten;
        try
        {
            isWritten = await writer.WriteConfigurationAsync(this, cancellationToken);
        }
        catch (Exception exception) when (throwOnFailure)
        {
            _isConfigurationUnsaved = true;
            throw new InvalidOperationException("The rotated Bluelink tokens could not be saved; saving is retried with the next call.", exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _isConfigurationUnsaved = true;
            _logger.LogWarning(exception, "Saving the Bluelink account failed; saving is retried with the next poll.");
            return;
        }

        _isConfigurationUnsaved = !isWritten;
        if (!isWritten)
        {
            _logger.LogWarning("The Bluelink account was not saved, because it is not stored in a file or the storage is not connected; saving is retried with each poll.");
        }
    }

    /// <remarks>Call it only while <see cref="_lock"/> is held.</remarks>
    private async Task SaveUnsavedConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_isConfigurationUnsaved)
        {
            await WriteConfigurationAsync(throwOnFailure: false, cancellationToken);
        }
    }

    /// <summary>Registers a device when none is stored, and stores its ID.</summary>
    /// <remarks>Call it only while <see cref="_lock"/> is held.</remarks>
    private async Task EnsureDeviceIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(DeviceId))
        {
            return;
        }

        var deviceId = await _apiClient.RegisterDeviceAsync(cancellationToken);
        lock (_credentialsLock)
        {
            DeviceId = deviceId;
            // The manager signs with the device ID.
            _tokenManager = null;
        }

        await WriteConfigurationAsync(throwOnFailure: false, cancellationToken);
    }

    /// <summary>Clears the device ID after Hyundai answered 4002, so that the next poll registers again.</summary>
    private void ResetDeviceId()
    {
        lock (_credentialsLock)
        {
            DeviceId = string.Empty;
            _tokenManager = null;
        }
    }

    private void UpdateAuthenticationState(BluelinkTokenManager tokenManager)
    {
        IsReauthenticationRequired = false;
        IsAuthenticated = true;
        AuthenticatedUser = Email;
        AuthenticatedAt = tokenManager.AuthenticatedAt ?? AuthenticatedAt;
    }

    /// <summary>Whether Hyundai rejected the credentials and neither the tokens nor the password changed since.</summary>
    private bool IsWaitingForNewCredentials()
    {
        if (!IsReauthenticationRequired)
        {
            return false;
        }

        lock (_credentialsLock)
        {
            return _rejectedCredentials is { } rejected && rejected.Tokens == Tokens && rejected.Password == Password;
        }
    }

    private void SetReauthenticationRequired(BluelinkReauthenticationRequiredException exception)
    {
        _logger.LogWarning("Bluelink re-authentication is required: {Reason}", exception.Message);
        lock (_credentialsLock)
        {
            _rejectedCredentials = (Tokens, Password);
        }

        IsReauthenticationRequired = true;
        IsAuthenticated = false;
        Status = ServiceStatus.Error;
        StatusMessage = $"{ReauthenticationRequiredMessage}: {exception.Message}";
    }
}
