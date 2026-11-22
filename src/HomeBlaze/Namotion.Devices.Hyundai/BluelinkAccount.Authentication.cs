using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Hyundai.Client;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Devices.Hyundai;

public partial class BluelinkAccount
{
    private const string ReauthenticationRequiredMessage = "Re-authentication required";
    private const string CredentialsReplacedMessage = "The credentials were replaced; the next poll uses them.";

    // Guards Email, Password, Pin, Tokens and DeviceId together with the _tokenManager fields and _rejectedCredentials,
    // so that a rotation, sign in or rejection of a manager whose credentials were replaced never affects the replacement.
    private readonly Lock _credentialsLock = new();

    private BluelinkTokenManager? _tokenManager;

    // The tokens the current manager holds and the password of its creation or of its last sign in. Kept when a device
    // ID change drops the manager, and set to the stored tokens when they could not be parsed.
    private string? _tokenManagerTokens;
    private string _tokenManagerPassword = string.Empty;

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
    /// Returns the token manager, kept across polls and configuration changes. It is replaced when the stored tokens
    /// change, or when a device registration or a device ID reset dropped it. New tokens read the vehicle list again.
    /// </summary>
    /// <remarks>Call it only while <see cref="_lock"/> is held, after <see cref="EnsureDeviceIdAsync"/>.</remarks>
    /// <exception cref="BluelinkReauthenticationRequiredException">The stored tokens are not valid.</exception>
    /// <exception cref="InvalidOperationException">No device ID is stored.</exception>
    private BluelinkTokenManager GetTokenManager()
    {
        BluelinkTokenManager? tokenManager = null;
        bool areTokensReplaced;
        lock (_credentialsLock)
        {
            if (_tokenManager is not null && Tokens == _tokenManagerTokens)
            {
                return _tokenManager;
            }

            if (string.IsNullOrEmpty(DeviceId))
            {
                throw new InvalidOperationException("No device is registered for the Bluelink account; the next poll registers one.");
            }

            areTokensReplaced = Tokens != _tokenManagerTokens;
            _tokenManager = null;
            _tokenManagerTokens = Tokens;
            _tokenManagerPassword = Password;

            BluelinkTokenSet tokens;
            try
            {
                tokens = BluelinkTokenSet.Parse(Tokens);
            }
            catch (FormatException)
            {
                throw new BluelinkReauthenticationRequiredException("The stored tokens are not valid. Connect the account again.");
            }

            tokenManager = new BluelinkTokenManager(_authClient, tokens, DeviceId, _timeProvider,
                rotatedTokens => PersistTokensAsync(tokenManager!, rotatedTokens),
                cancellationToken => LoginAsync(tokenManager!, cancellationToken));
            _tokenManager = tokenManager;
        }

        _controlToken = null;
        if (areTokensReplaced)
        {
            _vehicleListReadAt = null;
        }

        return tokenManager;
    }

    /// <summary>Whether the manager is the current one and the stored tokens are still the ones it holds.</summary>
    /// <remarks>Call it only while <see cref="_credentialsLock"/> is held.</remarks>
    private bool IsCurrent(BluelinkTokenManager? tokenManager) =>
        ReferenceEquals(tokenManager, _tokenManager) && Tokens == _tokenManagerTokens;

    /// <summary>Signs in with the stored email and password after Hyundai rejected the refresh token.</summary>
    /// <exception cref="BluelinkReauthenticationRequiredException">No password is stored, or Hyundai rejected the sign in.</exception>
    /// <exception cref="BluelinkCredentialsReplacedException">The tokens of the manager were replaced; it must not sign in.</exception>
    private Task<BluelinkCciTokens> LoginAsync(BluelinkTokenManager tokenManager, CancellationToken cancellationToken)
    {
        string email;
        string password;
        lock (_credentialsLock)
        {
            if (!IsCurrent(tokenManager))
            {
                throw new BluelinkCredentialsReplacedException(CredentialsReplacedMessage);
            }

            // The password is read when the sign in runs, so that a changed password applies without new tokens.
            email = Email;
            password = Password;
            _tokenManagerPassword = password;
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new BluelinkReauthenticationRequiredException("Hyundai rejected the refresh token, and no password is stored.");
        }

        _logger.LogInformation("Hyundai rejected the Bluelink refresh token; signing in again with the stored password.");
        return _authClient.LoginAsync(email, password, tokenManager.DeviceId, cancellationToken);
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
            if (!IsCurrent(tokenManager))
            {
                return;
            }

            Tokens = serialized;
            _tokenManagerTokens = serialized;
        }

        await WriteConfigurationAsync(throwOnFailure: true, CancellationToken.None);
    }

    /// <summary>Writes the configuration through the storage file that holds the account.</summary>
    /// <param name="throwOnFailure">
    /// Whether a failing writer throws, for the token manager to retry the write; otherwise the failure is logged and
    /// retried with the next poll.
    /// </param>
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

    /// <summary>Registers a device when none is stored, and stores its ID unless a replacement stored one meanwhile.</summary>
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
            if (!string.IsNullOrEmpty(DeviceId))
            {
                return;
            }

            DeviceId = deviceId;
            // The manager signs with the device ID.
            _tokenManager = null;
        }

        // Not canceled: the registered device ID must be stored even when the caller gives up.
        await WriteConfigurationAsync(throwOnFailure: false, CancellationToken.None);
    }

    /// <summary>Clears the device ID after Hyundai answered 4002 to a call of the manager, so that the next poll registers again.</summary>
    /// <remarks>A device ID that a replacement stored meanwhile is kept.</remarks>
    private void ResetDeviceId(BluelinkTokenManager? tokenManager)
    {
        lock (_credentialsLock)
        {
            if (tokenManager is null || DeviceId == tokenManager.DeviceId)
            {
                DeviceId = string.Empty;
                _tokenManager = null;
            }
        }
    }

    private void UpdateAuthenticationState(BluelinkTokenManager tokenManager)
    {
        IsReauthenticationRequired = false;
        IsAuthenticated = true;
        AuthenticatedUser = Email;
        AuthenticatedAt = tokenManager.AuthenticatedAt ?? AuthenticatedAt;
    }

    private void ClearAuthenticationState()
    {
        IsAuthenticated = false;
        AuthenticatedUser = null;
        AuthenticatedAt = null;
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

    /// <summary>
    /// Waits for new credentials after Hyundai rejected those of the manager, or of the stored tokens when the manager is
    /// null. A rejection of credentials that were replaced meanwhile is an ordinary failure, so that the next poll uses
    /// the replacement.
    /// </summary>
    /// <remarks>Call it only while <see cref="_lock"/> is held.</remarks>
    private void HandleRejection(BluelinkReauthenticationRequiredException exception, BluelinkTokenManager? tokenManager)
    {
        bool isCurrent;
        lock (_credentialsLock)
        {
            isCurrent = IsCurrent(tokenManager);
            if (isCurrent)
            {
                _rejectedCredentials = (Tokens, _tokenManagerPassword);
            }
        }

        if (!isCurrent)
        {
            ReportCredentialsReplaced();
            return;
        }

        _logger.LogWarning("Bluelink re-authentication is required: {Reason}", exception.Message);
        IsReauthenticationRequired = true;
        IsAuthenticated = false;
        Status = ServiceStatus.Error;
        StatusMessage = $"{ReauthenticationRequiredMessage}: {exception.Message}";
    }

    private void ReportCredentialsReplaced()
    {
        _logger.LogInformation("The Bluelink credentials were replaced while the previous ones were in use; the next poll uses them.");
        Status = ServiceStatus.Error;
        StatusMessage = CredentialsReplacedMessage;
    }
}
