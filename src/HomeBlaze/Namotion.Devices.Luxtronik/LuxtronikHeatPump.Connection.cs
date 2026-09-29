using HomeBlaze.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Devices.Luxtronik.Model;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Devices.Luxtronik;

public partial class LuxtronikHeatPump : BackgroundService, IModbusDiscovery, IConfigurable
{
    private const int FirmwareAddress = 10400;

    /// <summary>
    /// The minimum <see cref="PollingInterval"/> in seconds; shorter intervals are raised to it.
    /// </summary>
    public const int MinimumPollingIntervalSeconds = 2;

    // Protects the controller from a hand-edited configuration that would poll it continuously.
    private static readonly TimeSpan MinimumPollingInterval = TimeSpan.FromSeconds(MinimumPollingIntervalSeconds);

    private const int UnknownFeatureMask = -1;

    private readonly SemaphoreSlim _configurationChanged = new(0, 1);

    // Written by the discovery on the source's thread, read by the status loop.
    private int _discoveredFeatureMask = UnknownFeatureMask;

    /// <summary>
    /// Gets how often <see cref="DiscoverAsync"/> ran.
    /// </summary>
    internal int DiscoveryCount { get; private set; }

    /// <summary>
    /// Reads the firmware version and active functions, creates or removes the optional function subjects, and excludes the registers the controller does not provide.
    /// </summary>
    public async Task DiscoverAsync(ModbusDiscoveryContext context, CancellationToken cancellationToken)
    {
        DiscoveryCount++;

        var versionRegisters = await context.ReadInputRegistersAsync(FirmwareAddress, 3, cancellationToken: cancellationToken).ConfigureAwait(false);
        var firmwareVersion = new Version(versionRegisters[0], versionRegisters[1], versionRegisters[2]);
        new PropertyReference(this, nameof(SoftwareVersion))
            .SetValueFromSource(context.Source, null, null, firmwareVersion.ToString());

        IReadOnlySet<LuxtronikFeature>? configuredFeatures = null;
        var featureMask = UnknownFeatureMask;
        try
        {
            var flags = await context.ReadDiscreteInputsAsync(Features.BaseAddress, LuxtronikGating.FeatureFlagCount, cancellationToken: cancellationToken).ConfigureAwait(false);
            configuredFeatures = LuxtronikGating.GetConfiguredFeatures(flags);
            featureMask = LuxtronikGating.GetFeatureMask(flags);
        }
        catch (ModbusResponseException exception) when (exception.IsPermanentRejection)
        {
            _logger.LogInformation(
                exception,
                "Luxtronik {HostAddress} does not report its configured functions (exception code {ExceptionCode}); values of unconfigured functions are shown as unavailable instead.",
                HostAddress, exception.ExceptionCode);
        }

        UpdateFunctionSubjects(configuredFeatures);

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

        Volatile.Write(ref _discoveredFeatureMask, featureMask);
        _logger.LogInformation("Luxtronik {HostAddress} runs firmware {FirmwareVersion}.", HostAddress, firmwareVersion);
    }

    /// <summary>
    /// Creates the subjects of the active optional functions, keeps existing ones, and removes the inactive ones; all exist when <paramref name="activeFeatures"/> is unknown (<c>null</c>).
    /// </summary>
    internal void UpdateFunctionSubjects(IReadOnlySet<LuxtronikFeature>? activeFeatures)
    {
        Cooling = GetFunctionSubject(activeFeatures, LuxtronikFeature.Cooling, LuxtronikFeature.None,
            Cooling, static () => new LuxtronikCooling());
        Pool = GetFunctionSubject(activeFeatures, LuxtronikFeature.Pool, LuxtronikFeature.None,
            Pool, static () => new LuxtronikPool());
        Solar = GetFunctionSubject(activeFeatures, LuxtronikFeature.Solar, LuxtronikFeature.None,
            Solar, static () => new LuxtronikSolar());
        RoomControl = GetFunctionSubject(activeFeatures, LuxtronikFeature.RoomControlUnit, LuxtronikFeature.None,
            RoomControl, static () => new LuxtronikRoomControl());
        MixingCircuit1 = GetFunctionSubject(activeFeatures, LuxtronikFeature.MixingCircuit1Heating, LuxtronikFeature.MixingCircuit1Cooling,
            MixingCircuit1, static () => new LuxtronikMixingCircuit(1));
        MixingCircuit2 = GetFunctionSubject(activeFeatures, LuxtronikFeature.MixingCircuit2Heating, LuxtronikFeature.MixingCircuit2Cooling,
            MixingCircuit2, static () => new LuxtronikMixingCircuit(2));
        MixingCircuit3 = GetFunctionSubject(activeFeatures, LuxtronikFeature.MixingCircuit3Heating, LuxtronikFeature.MixingCircuit3Cooling,
            MixingCircuit3, static () => new LuxtronikMixingCircuit(3));
    }

    // LuxtronikFeature.None is never active, so it serves as "no alternative".
    private static TSubject? GetFunctionSubject<TSubject>(
        IReadOnlySet<LuxtronikFeature>? activeFeatures,
        LuxtronikFeature feature,
        LuxtronikFeature alternativeFeature,
        TSubject? current,
        Func<TSubject> create)
        where TSubject : class
    {
        var isActive = activeFeatures is null || activeFeatures.Contains(feature) || activeFeatures.Contains(alternativeFeature);
        return isActive ? current ?? create() : null;
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
    /// error, which stays set while it reconnects. While the connection is healthy, a non-zero controller error number is
    /// reported in <see cref="StatusMessage"/> and the status stays <see cref="ServiceStatus.Running"/>.
    /// </summary>
    internal void UpdateStatus(ModbusClientDiagnostics diagnostics)
    {
        IsConnected = diagnostics.IsOperational == true;
        LastUpdated = diagnostics.Polling.LastPollTime ?? LastUpdated;

        if (IsConnected)
        {
            var errorNumber = OperatingStatus.ErrorNumber;
            Status = ServiceStatus.Running;
            StatusMessage = errorNumber is > 0 ? $"Heat pump error {errorNumber}" : null;
        }
        else
        {
            var lastError = diagnostics.LastError;
            Status = lastError is null ? ServiceStatus.Starting : ServiceStatus.Error;
            StatusMessage = lastError?.Message ?? "Connecting...";
        }
    }

    /// <summary>
    /// Gets the configured <see cref="PollingInterval"/>, raised to <see cref="MinimumPollingIntervalSeconds"/>.
    /// </summary>
    internal TimeSpan GetEffectivePollingInterval()
    {
        var pollingInterval = PollingInterval;
        return pollingInterval < MinimumPollingInterval ? MinimumPollingInterval : pollingInterval;
    }

    private async Task RunSourceAsync(string hostAddress, CancellationToken stoppingToken)
    {
        // Captured once, so a configuration edit applies only through the restart it signals.
        var pollingInterval = GetEffectivePollingInterval();

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

        // Discards the previous source's discovery, whose mismatch with the polled flags would restart this source again.
        Volatile.Write(ref _discoveredFeatureMask, UnknownFeatureMask);

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
                if (HaveFeaturesChanged(hostAddress) ||
                    await WaitForConfigurationChangeAsync(pollingInterval, stoppingToken).ConfigureAwait(false))
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

    /// <summary>
    /// Gets whether the polled <see cref="Features"/> differ from the flags the last discovery read, which excluded the
    /// registers of the inactive functions. <c>false</c> until both are known.
    /// </summary>
    private bool HaveFeaturesChanged(string hostAddress)
    {
        var discoveredMask = Volatile.Read(ref _discoveredFeatureMask);
        if (discoveredMask == UnknownFeatureMask || Features.GetFeatureMask() is not { } polledMask || polledMask == discoveredMask)
        {
            return false;
        }

        _logger.LogInformation(
            "Luxtronik {HostAddress} changed its active functions ({ChangedFeatures}); discovering its registers again.",
            hostAddress, LuxtronikGating.GetFeatureNames(polledMask ^ discoveredMask));
        return true;
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
