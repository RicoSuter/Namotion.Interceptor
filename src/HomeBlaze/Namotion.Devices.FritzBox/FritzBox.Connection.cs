using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Devices.FritzBox.Client;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Devices.FritzBox;

public partial class FritzBox
{
    private const int MaximumConsecutivePollFailures = 3;
    private const string NoPasswordMessage = "No password configured.";
    private const string UnsavedCertificateWarning = "The trusted certificate could not be saved and is trusted anew after a restart.";

    private static readonly TimeSpan InitialLoginRetryDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumLoginRetryDelay = TimeSpan.FromHours(2);

    // Only the loop writes _connection. A command holds the gate while it uses the connection, so the loop clears
    // and disposes a connection only after the command completes.
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly Lock _wlanSwitchStateLock = new();
    private FritzBoxConnection? _connection;
    private long _wlanCommandGeneration;

    // Read and written by the polling loop only.
    private string? _stateHostAddress;
    private string? _unsavedPublicKeyPin;
    private TimeSpan _loginRetryDelay = InitialLoginRetryDelay;

    /// <summary>
    /// Raised by every successful WLAN command, so a poll that read the switch state before it does not write it back.
    /// </summary>
    internal long WlanCommandGeneration => Interlocked.Read(ref _wlanCommandGeneration);

    private string? CertificateWarning =>
        _unsavedPublicKeyPin is not null && PublicKeyPin == _unsavedPublicKeyPin ? UnsavedCertificateWarning : null;

    internal void AdvanceWlanCommandGeneration()
    {
        lock (_wlanSwitchStateLock)
        {
            Interlocked.Increment(ref _wlanCommandGeneration);
        }
    }

    /// <summary>
    /// Enables or disables the guest WLAN. Throws <see cref="InvalidOperationException"/> while the router is not
    /// connected.
    /// </summary>
    internal async Task SetGuestWlanEnabledAsync(FritzBoxGuestWlan wlan, bool isEnabled, CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            var connection = _connection
                ?? throw new InvalidOperationException("The FRITZ!Box is not connected. Try again when its status is Running.");
            var serviceType = wlan.ServiceType;
            await connection.InvokeAsync(serviceType, "SetEnable", [("NewEnable", isEnabled ? "1" : "0")], cancellationToken);
            AdvanceWlanCommandGeneration();
            var info = await connection.ReadOrDefaultAsync(serviceType, "GetInfo", null, cancellationToken);
            wlan.IsOn = info?.GetBoolean("NewEnable") ?? isEnabled;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var hostAddress = UpdateHostAddress();
            if (hostAddress is null)
            {
                IsConnected = false;
                Status = ServiceStatus.Stopped;
                StatusMessage = "No host address configured";
                await WaitForConfigurationChangeAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }

            if (string.IsNullOrEmpty(Password))
            {
                // A login without a password cannot succeed, and every failed one lengthens the router's lockout.
                _logger.LogWarning("FRITZ!Box {HostAddress} has no password configured.", hostAddress);
                SetError(NoPasswordMessage);
                await WaitForConfigurationChangeAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }

            if (await ConnectAndPollAsync(hostAddress, stoppingToken) is { } retryDelay &&
                await WaitForConfigurationChangeAsync(retryDelay, stoppingToken))
            {
                _loginRetryDelay = InitialLoginRetryDelay;
            }
        }

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <returns>The configured host address, or null when none is configured. Clears the state of the previous
    /// router when the address changed.</returns>
    private string? UpdateHostAddress()
    {
        var hostAddress = StringValues.NullIfBlank(HostAddress);
        var stateHostAddress = hostAddress ?? string.Empty;
        if (_stateHostAddress is not null && stateHostAddress != _stateHostAddress)
        {
            ClearRouterState();
        }

        _stateHostAddress = stateHostAddress;
        return hostAddress;
    }

    /// <summary>
    /// Connects and polls until the configuration changes, the service stops or the connection fails. The
    /// connection is disposed before this returns.
    /// </summary>
    /// <returns>How long to wait for a configuration change before the next attempt, or null to reconnect at once.</returns>
    private async Task<TimeSpan?> ConnectAndPollAsync(string hostAddress, CancellationToken stoppingToken)
    {
        FritzBoxConnection? connection = null;
        try
        {
            Status = ServiceStatus.Starting;
            StatusMessage = "Connecting...";
            connection = await FritzBoxConnection.OpenAsync(
                new FritzBoxConnectionOptions(hostAddress, Username, Password, StringValues.NullIfBlank(PublicKeyPin)) { DescriptionPort = DescriptionPort },
                _logger,
                stoppingToken);
            await connection.CheckLoginAsync(stoppingToken);
            _loginRetryDelay = InitialLoginRetryDelay;
            await TrustCertificateAsync(connection, stoppingToken);
            await connection.ResolveInternetServiceAsync(stoppingToken);
            Volatile.Write(ref _connection, connection);

            Status = ServiceStatus.Running;
            StatusMessage = CertificateWarning;
            await RunPollingLoopAsync(connection, stoppingToken);
            return null;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return null;
        }
        catch (FritzBoxLoginException exception)
        {
            return OnLoginRefused(hostAddress, exception);
        }
        catch (FritzBoxCertificateException exception)
        {
            OnCertificateRejected(hostAddress, exception);
            return Timeout.InfiniteTimeSpan;
        }
        catch (FritzBoxFirmwareException exception)
        {
            OnFirmwareUnsupported(hostAddress, exception);
            return Timeout.InfiniteTimeSpan;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "FRITZ!Box {HostAddress} connection failed.", hostAddress);
            SetError(exception.Message);
            return EffectiveRetryInterval;
        }
        finally
        {
            IsConnected = false;
            await ReleaseConnectionAsync(connection);
        }
    }

    /// <returns>How long to wait before the next login.</returns>
    private TimeSpan OnLoginRefused(string hostAddress, FritzBoxLoginException exception)
    {
        // Every failed login lengthens the router's lockout, so the next attempt waits long unless the user changes
        // the configuration.
        var retryDelay = _loginRetryDelay;
        _loginRetryDelay = GetNextLoginRetryDelay(retryDelay);
        _logger.LogWarning("FRITZ!Box {HostAddress} refused the login ({Reason}); retrying in {RetryDelay}.", hostAddress, exception.Message, retryDelay);
        SetError(exception.Message);
        return retryDelay;
    }

    private void OnCertificateRejected(string hostAddress, FritzBoxCertificateException exception)
    {
        var presentedPin = exception.Presented?.Pin;
        _logger.LogWarning("FRITZ!Box {HostAddress} presented the untrusted certificate key {PublicKeyPin}.", hostAddress, presentedPin);
        PresentedPublicKeyPin = presentedPin;
        PresentedCertificateFingerprint = exception.Presented?.Fingerprint;
        SetError(exception.Message);
    }

    private void OnFirmwareUnsupported(string hostAddress, FritzBoxFirmwareException exception)
    {
        _logger.LogWarning("FRITZ!Box {HostAddress}: {Message}", hostAddress, exception.Message);
        SetError(exception.Message);
    }

    private async Task RunPollingLoopAsync(FritzBoxConnection connection, CancellationToken stoppingToken)
    {
        var failures = 0;
        while (true)
        {
            try
            {
                var generation = WlanCommandGeneration;
                var snapshot = await FritzBoxPoller.PollAsync(connection, stoppingToken);
                ApplySnapshot(snapshot, generation);
                failures = 0;
                IsConnected = true;
                LastUpdated = DateTimeOffset.UtcNow;
                StatusMessage = CertificateWarning;
                TryApplyDevicePaths();
            }
            catch (Exception exception) when (IsTolerated(exception, stoppingToken))
            {
                failures++;
                if (failures >= MaximumConsecutivePollFailures)
                {
                    throw;
                }

                StatusMessage = $"Data may be stale ({failures} of {MaximumConsecutivePollFailures}): {exception.Message}";
                _logger.LogWarning(
                    exception,
                    "FRITZ!Box poll failed ({FailureCount} in a row, connection reset after {MaximumFailureCount}).",
                    failures,
                    MaximumConsecutivePollFailures);
            }

            if (await WaitForConfigurationChangeAsync(EffectivePollingInterval, stoppingToken))
            {
                return;
            }

            stoppingToken.ThrowIfCancellationRequested();
        }
    }

    private static TimeSpan GetNextLoginRetryDelay(TimeSpan loginRetryDelay)
    {
        var doubled = loginRetryDelay * 2;
        return doubled > MaximumLoginRetryDelay ? MaximumLoginRetryDelay : doubled;
    }

    private static bool IsTolerated(Exception exception, CancellationToken stoppingToken) =>
        exception is not (FritzBoxLoginException or FritzBoxCertificateException or FritzBoxFirmwareException) &&
        !(exception is OperationCanceledException && stoppingToken.IsCancellationRequested);

    private async Task TrustCertificateAsync(FritzBoxConnection connection, CancellationToken cancellationToken)
    {
        PresentedPublicKeyPin = null;
        PresentedCertificateFingerprint = null;
        if (StringValues.NullIfBlank(PublicKeyPin) is not null)
        {
            await RetryUnsavedCertificateAsync(cancellationToken);
            return;
        }

        if (connection.TrustedPublicKeyPin is not { } trustedPin)
        {
            return;
        }

        PublicKeyPin = trustedPin;
        _logger.LogInformation("Trusting the certificate key {PublicKeyPin} of FRITZ!Box {HostAddress} on first use.", trustedPin, HostAddress);

        var isWritten = await TryWriteConfigurationAsync(cancellationToken);
        _unsavedPublicKeyPin = isWritten ? null : trustedPin;
        if (!isWritten)
        {
            _logger.LogWarning("The trusted certificate key of FRITZ!Box {HostAddress} could not be saved.", HostAddress);
        }
    }

    /// <summary>
    /// Saves a key trusted earlier that could not be saved then, so the warning ends once a writer stores it.
    /// </summary>
    private async Task RetryUnsavedCertificateAsync(CancellationToken cancellationToken)
    {
        if (_unsavedPublicKeyPin is null)
        {
            return;
        }

        if (_unsavedPublicKeyPin != PublicKeyPin || await TryWriteConfigurationAsync(cancellationToken))
        {
            _unsavedPublicKeyPin = null;
        }
    }

    /// <returns>Whether a configuration writer stored the configuration.</returns>
    private async Task<bool> TryWriteConfigurationAsync(CancellationToken cancellationToken)
    {
        var writer = this.TryGetFirstParent<IConfigurationWriter>();
        if (writer is null)
        {
            return false;
        }

        try
        {
            return await writer.WriteConfigurationAsync(this, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Writing the configuration of FRITZ!Box {HostAddress} failed.", HostAddress);
            return false;
        }
    }

    private async Task ReleaseConnectionAsync(FritzBoxConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        await _connectionGate.WaitAsync(CancellationToken.None);
        try
        {
            _connection = null;
        }
        finally
        {
            _connectionGate.Release();
        }

        connection.Dispose();
    }

    private void SetError(string message)
    {
        IsConnected = false;
        Status = ServiceStatus.Error;
        StatusMessage = message;
    }
}
