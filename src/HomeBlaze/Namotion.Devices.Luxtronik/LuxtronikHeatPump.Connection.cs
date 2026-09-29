using HomeBlaze.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Devices.Luxtronik;

public partial class LuxtronikHeatPump : BackgroundService, IModbusDiscovery, IConfigurable
{
    private const int FirmwareAddress = 10400;
    private const int FeatureFlagsAddress = 10000;

    private readonly SemaphoreSlim _configurationChanged = new(0, 1);

    /// <summary>
    /// Reads the firmware version and configured functions, and excludes the registers the controller does not provide.
    /// </summary>
    public async Task DiscoverAsync(ModbusDiscoveryContext context, CancellationToken cancellationToken)
    {
        var versionRegisters = await context.ReadInputRegistersAsync(FirmwareAddress, 3, cancellationToken: cancellationToken).ConfigureAwait(false);
        var firmwareVersion = new Version(versionRegisters[0], versionRegisters[1], versionRegisters[2]);
        new PropertyReference(this, nameof(SoftwareVersion))
            .SetValueFromSource(context.Source, null, null, firmwareVersion.ToString());

        IReadOnlySet<LuxtronikFeature>? configuredFeatures = null;
        try
        {
            var flags = await context.ReadDiscreteInputsAsync(FeatureFlagsAddress, LuxtronikGating.FeatureFlagCount, cancellationToken: cancellationToken).ConfigureAwait(false);
            configuredFeatures = LuxtronikGating.GetConfiguredFeatures(flags);
        }
        catch (ModbusResponseException exception) when (exception.IsPermanentRejection)
        {
            _logger.LogInformation(
                exception,
                "Luxtronik {HostAddress} does not report its configured functions (exception code {ExceptionCode}); values of unconfigured functions are shown as unavailable instead.",
                HostAddress, exception.ExceptionCode);
        }

        var registeredSubject = this.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("The heat pump is not registered. Attach it to a subject graph with a registry.");

        foreach (var property in registeredSubject.GetAllProperties())
        {
            var isUnreadableFeatureFlag = configuredFeatures is null && ReferenceEquals(property.Subject, Features);
            if (isUnreadableFeatureFlag || !LuxtronikGating.IsSupported(property, firmwareVersion, configuredFeatures))
            {
                context.ExcludeProperty(property.Reference);
            }
        }

        _logger.LogInformation("Luxtronik {HostAddress} runs firmware {FirmwareVersion}.", HostAddress, firmwareVersion);
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            _configurationChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // A change is already signaled and not yet consumed, which covers this one.
        }
        catch (ObjectDisposedException)
        {
            // The device is disposed, so there is no source left to restart.
        }

        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var hostAddress = HostAddress;
            if (string.IsNullOrWhiteSpace(hostAddress))
            {
                Status = ServiceStatus.Stopped;
                StatusMessage = "No host address configured";
                await WaitForConfigurationChangeAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                continue;
            }

            await RunSourceAsync(hostAddress, stoppingToken).ConfigureAwait(false);
        }

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <summary>
    /// Mirrors the source diagnostics into <see cref="IsConnected"/>, <see cref="Status"/>, <see cref="StatusMessage"/>
    /// and <see cref="LastUpdated"/>. Disconnected reads as <see cref="ServiceStatus.Error"/> once the source reported an
    /// error, which stays set while it reconnects. While the connection is healthy, a non-zero controller error code is
    /// reported in <see cref="StatusMessage"/> and the status stays <see cref="ServiceStatus.Running"/>.
    /// </summary>
    internal void UpdateStatus(ModbusClientDiagnostics diagnostics)
    {
        IsConnected = diagnostics.IsOperational == true;
        LastUpdated = diagnostics.Polling.LastPollTime ?? LastUpdated;

        if (IsConnected)
        {
            var errorCode = OperatingStatus.ErrorCode;
            Status = ServiceStatus.Running;
            StatusMessage = errorCode is > 0 ? $"Heat pump error {errorCode}" : null;
        }
        else
        {
            var lastError = diagnostics.LastError;
            Status = lastError is null ? ServiceStatus.Starting : ServiceStatus.Error;
            StatusMessage = lastError?.Message ?? "Connecting...";
        }
    }

    private async Task RunSourceAsync(string hostAddress, CancellationToken stoppingToken)
    {
        // Captured once, so a configuration edit applies only through the restart it signals.
        var pollingInterval = PollingInterval;

        ModbusSubjectClientSource source;
        try
        {
            source = this.CreateModbusClientSource(
                new ModbusClientConfiguration { Host = hostAddress, Port = Port, PollingInterval = pollingInterval },
                _logger);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _logger.LogError(exception, "Luxtronik heat pump {HostAddress} has an invalid configuration.", hostAddress);
            Status = ServiceStatus.Error;
            StatusMessage = exception.Message;
            await WaitForConfigurationChangeAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        var hasFailed = false;
        try
        {
            Status = ServiceStatus.Starting;
            StatusMessage = "Connecting...";

            await this.AttachHostedServiceAsync(source, stoppingToken).ConfigureAwait(false);

            // Source diagnostics are not tracked properties, so they are mirrored into this device's state.
            while (!stoppingToken.IsCancellationRequested)
            {
                UpdateStatus(source.Diagnostics);
                if (await WaitForConfigurationChangeAsync(pollingInterval, stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: the source is released below.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Luxtronik heat pump {HostAddress} failed.", hostAddress);
            Status = ServiceStatus.Error;
            StatusMessage = exception.Message;
            hasFailed = true;
        }
        finally
        {
            await ReleaseSourceAsync(source, hostAddress).ConfigureAwait(false);
        }

        if (hasFailed)
        {
            await WaitForConfigurationChangeAsync(pollingInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ReleaseSourceAsync(ModbusSubjectClientSource source, string hostAddress)
    {
        try
        {
            await this.DetachHostedServiceAsync(source, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to detach the Modbus source of {HostAddress}.", hostAddress);
        }

        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to dispose the Modbus source of {HostAddress}.", hostAddress);
        }

        IsConnected = false;
    }

    private async Task<bool> WaitForConfigurationChangeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _configurationChanged.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _configurationChanged.Dispose();
    }
}
